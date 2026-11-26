using System.Collections.Concurrent;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;

namespace KodAnalist;

/// <summary>
/// Şirket içi Active Directory (LDAP) ile e-posta + şifre girişi ayarları ("Ldap" bölümü).
/// Server boşsa LDAP girişi kapalıdır.
/// </summary>
public sealed class LdapOptions
{
    /// <summary>Domain denetleyicisinin adı (ör. "dc01.promena.local" veya domain adı "promena.local").</summary>
    public string Server { get; set; } = "";

    /// <summary>636: LDAPS (şifreli, önerilen). 389: şifresiz LDAP (yalnızca UseSsl=false iken).</summary>
    public int Port { get; set; } = 636;

    /// <summary>
    /// Şifreli bağlantı (LDAPS). Kapatmak şifrenin ağda açık metin gitmesi demektir; yalnızca test içindir.
    /// Sunucu sertifikası bilgisayarın güvendiği bir sertifika olmalıdır (domain bilgisayarlarında kurumsal CA zaten güvenilir).
    /// </summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>NetBIOS domain adı (ör. "PROMENA"). Doluysa UPN ile giriş olmazsa "PROMENA\kullanici" de denenir.</summary>
    public string? Domain { get; set; }

    /// <summary>Kullanıcı aramasının yapılacağı kök (ör. "DC=promena,DC=local"). Boşsa sunucunun varsayılan kökü kullanılır.</summary>
    public string? BaseDn { get; set; }

    /// <summary>
    /// Bağlanırken denenecek kullanıcı adı biçimleri, sırayla. {email}: yazılan e-posta, {user}: e-postanın @ öncesi,
    /// {domain}: Domain ayarı. Boşsa Active Directory için varsayılanlar: "{email}" (UPN) ve "{domain}\{user}".
    /// </summary>
    public string[] BindFormats { get; set; } = [];

    /// <summary>Yalnızca bu uzantılardaki e-postalar kabul edilir (ör. "promena.net"). Boşsa uzantı kısıtı yok.</summary>
    public string[] AllowedEmailDomains { get; set; } = [];

    /// <summary>İsteğe bağlı: kullanıcı bu gruplardan birinin üyesi olmalı (grup DN, ör. "CN=Analistler,OU=Gruplar,DC=promena,DC=local").</summary>
    public string[] AllowedGroupDns { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Server);
}

public sealed record LdapUser(string Name, string Email, IReadOnlyList<string> Groups);

public enum LdapLoginStatus { Success, InvalidCredentials, NotAllowed, TooManyAttempts, ServerUnavailable }

public sealed record LdapLoginResult(LdapLoginStatus Status, LdapUser? User = null);

/// <summary>
/// Kullanıcının e-posta ve şifresini, kendi adına LDAP'a bağlanmayı (bind) deneyerek doğrular. Şifre hiçbir yere
/// kaydedilmez ve loglanmaz. Ayrı bir servis hesabı gerekmez.
///
/// Art arda hatalı denemeler uygulamada durdurulur: aynı e-postayla veya aynı adresten kısa sürede çok sayıda hatalı
/// deneme olursa bir süre AD'ye hiç sorulmaz. Böylece AD'nin hesap kilitleme kuralı uygulama üzerinden tetiklenmez.
/// </summary>
public sealed class LdapAuthService(LdapOptions options, ILogger<LdapAuthService> logger)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, List<DateTime>> _failures = new();

    public async Task<LdapLoginResult> LoginAsync(string? email, string? password, string? clientIp)
    {
        email = (email ?? "").Trim();
        if (email.Length is 0 or > 256 || string.IsNullOrEmpty(password) || password.Length > 256 || !email.Contains('@'))
            return new(LdapLoginStatus.InvalidCredentials);

        var emailKey = "e:" + email.ToLowerInvariant();
        var ipKey = "ip:" + (clientIp ?? "?");
        if (IsBlocked(emailKey) || IsBlocked(ipKey, MaxFailures * 4))
            return new(LdapLoginStatus.TooManyAttempts);

        var domainPart = email[(email.IndexOf('@') + 1)..];
        if (options.AllowedEmailDomains.Length > 0 &&
            !options.AllowedEmailDomains.Any(d => string.Equals(d.Trim().TrimStart('@'), domainPart, StringComparison.OrdinalIgnoreCase)))
        {
            RecordFailure(emailKey); RecordFailure(ipKey);
            return new(LdapLoginStatus.InvalidCredentials);
        }

        LdapLoginResult result;
        try
        {
            // LDAP kütüphanesi eşzamanlı çalışır; isteği bekletmemek için arka planda.
            result = await Task.Run(() => Authenticate(email, password));
        }
        catch (Exception ex)
        {
            logger.LogError("LDAP sunucusuna ulaşılamadı ({Server}:{Port}): {Error}", options.Server, options.Port, ex.Message);
            return new(LdapLoginStatus.ServerUnavailable);
        }

        if (result.Status == LdapLoginStatus.Success)
        {
            _failures.TryRemove(emailKey, out _);
            logger.LogInformation("Giriş: {Email}", result.User!.Email);
        }
        else if (result.Status is LdapLoginStatus.InvalidCredentials or LdapLoginStatus.NotAllowed)
        {
            RecordFailure(emailKey); RecordFailure(ipKey);
            logger.LogWarning("Başarısız giriş ({Status}): {Email}", result.Status, email);
        }
        return result;
    }

    private LdapLoginResult Authenticate(string email, string password)
    {
        var user = email[..email.IndexOf('@')];
        var formats = options.BindFormats.Length > 0
            ? options.BindFormats
            : string.IsNullOrWhiteSpace(options.Domain) ? ["{email}"] : ["{email}", "{domain}\\{user}"];

        foreach (var format in formats)
        {
            var bindName = format.Replace("{email}", email).Replace("{user}", user).Replace("{domain}", options.Domain ?? "");
            using var connection = Connect();
            try
            {
                connection.Bind(new NetworkCredential(bindName, password));
            }
            catch (LdapException ex) when (ex.ErrorCode == 49)   // invalidCredentials: bu biçimle olmadı, sıradakini dene
            {
                continue;
            }

            // Şifre doğru. Kullanıcının adını, e-postasını ve gruplarını kendi yetkisiyle oku.
            var entry = FindUser(connection, email, user);
            var name = Attr(entry, "displayName") ?? Attr(entry, "cn") ?? email;
            var mail = Attr(entry, "mail") ?? Attr(entry, "userPrincipalName") ?? email;
            var groups = entry?.Attributes["memberOf"] is { } memberOf
                ? memberOf.GetValues(typeof(string)).Cast<string>().ToList()
                : [];

            if (options.AllowedGroupDns.Length > 0 && !InAllowedGroup(connection, entry, groups))
                return new(LdapLoginStatus.NotAllowed);
            return new(LdapLoginStatus.Success, new LdapUser(name, mail, groups));
        }
        return new(LdapLoginStatus.InvalidCredentials);
    }

    private LdapConnection Connect()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(options.Server, options.Port))
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = options.UseSsl;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        return connection;
    }

    private SearchResultEntry? FindUser(LdapConnection connection, string email, string user)
    {
        try
        {
            var baseDn = string.IsNullOrWhiteSpace(options.BaseDn) ? DefaultNamingContext(connection) : options.BaseDn;
            if (string.IsNullOrEmpty(baseDn)) return null;
            var filter = $"(|(userPrincipalName={Escape(email)})(mail={Escape(email)})(sAMAccountName={Escape(user)})(uid={Escape(user)}))";
            var response = (SearchResponse)connection.SendRequest(new SearchRequest(baseDn, filter, SearchScope.Subtree,
                "displayName", "cn", "mail", "userPrincipalName", "memberOf"));
            return response.Entries.Count > 0 ? response.Entries[0] : null;
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            logger.LogWarning("Kullanıcı bilgileri okunamadı: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>Doğrudan üyelik (memberOf) veya Active Directory'de iç içe grup üyeliği.</summary>
    private bool InAllowedGroup(LdapConnection connection, SearchResultEntry? entry, List<string> groups)
    {
        if (groups.Any(g => options.AllowedGroupDns.Contains(g, StringComparer.OrdinalIgnoreCase))) return true;
        if (entry == null) return false;
        foreach (var group in options.AllowedGroupDns)
        {
            try
            {
                // AD'ye özgü "LDAP_MATCHING_RULE_IN_CHAIN": iç içe grupları da kapsar.
                var response = (SearchResponse)connection.SendRequest(new SearchRequest(entry.DistinguishedName,
                    $"(memberOf:1.2.840.113556.1.4.1941:={Escape(group)})", SearchScope.Base, "cn"));
                if (response.Entries.Count > 0) return true;
            }
            catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
            {
                // AD olmayan sunucular bu kuralı desteklemez; doğrudan üyelik kontrolü yeterli.
            }
        }
        return false;
    }

    private static string? DefaultNamingContext(LdapConnection connection)
    {
        var response = (SearchResponse)connection.SendRequest(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "namingContexts"));
        if (response.Entries.Count == 0) return null;
        return Attr(response.Entries[0], "defaultNamingContext") ?? Attr(response.Entries[0], "namingContexts");
    }

    private static string? Attr(SearchResultEntry? entry, string name)
    {
        if (entry?.Attributes[name] is not { Count: > 0 } attr) return null;
        return attr[0] switch
        {
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b),
            var o => o?.ToString(),
        };
    }

    /// <summary>LDAP arama filtresi için özel karakterleri kaçırır (RFC 4515).</summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => @"\5c", '*' => @"\2a", '(' => @"\28", ')' => @"\29", '\0' => @"\00",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    private bool IsBlocked(string key, int max = MaxFailures)
    {
        if (!_failures.TryGetValue(key, out var list)) return false;
        lock (list)
        {
            list.RemoveAll(t => DateTime.UtcNow - t > FailureWindow);
            return list.Count >= max;
        }
    }

    private void RecordFailure(string key)
    {
        var list = _failures.GetOrAdd(key, _ => []);
        lock (list) list.Add(DateTime.UtcNow);
    }
}
