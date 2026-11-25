namespace KodAnalist;

/// <summary>Analiz edilecek bir kod deposu.</summary>
public sealed class RepoConfig
{
    /// <summary>Modelin araçlarda kullanacağı kısa ad (ör. "ProSourceCore").</summary>
    public string Name { get; set; } = "";

    /// <summary>Deponun diskteki tam yolu.</summary>
    public string Path { get; set; } = "";

    /// <summary>Deponun ne içerdiğini anlatan kısa açıklama (modele yol gösterir).</summary>
    public string Description { get; set; } = "";
}

/// <summary>Microsoft Foundry üzerindeki Claude bağlantı ayarları.</summary>
public sealed class FoundryOptions
{
    /// <summary>Foundry kaynak adı (https://{ResourceName}.services.ai.azure.com).</summary>
    public string ResourceName { get; set; } = "";

    /// <summary>Foundry'deki model dağıtımının (deployment) adı.</summary>
    public string Deployment { get; set; } = "claude-opus-5-5";

    /// <summary>
    /// API anahtarı. Dosyaya yazılmaz; ANTHROPIC_FOUNDRY_API_KEY ortam değişkeninden
    /// veya Azure Key Vault gibi bir gizli bilgi kaynağından gelir. Boş bırakılırsa
    /// Microsoft Entra ID (DefaultAzureCredential) kullanılır.
    /// </summary>
    public string? ApiKey { get; set; }
}

public sealed class AgentOptions
{
    /// <summary>Bir soru için izin verilen en fazla araç turu.</summary>
    public int MaxToolTurns { get; set; } = 40;

    /// <summary>Taslak cevap, ikinci bir aşamada koda karşı doğrulansın mı.</summary>
    public bool VerifyAnswers { get; set; } = true;

    /// <summary>
    /// Doğrulama aşamasında izin verilen en fazla araç turu. Doğrulayıcı kanıt paketiyle çalıştığı için
    /// yalnızca pakette olmayan yerler için birkaç ek okuma yapması beklenir.
    /// </summary>
    public int VerifyMaxToolTurns { get; set; } = 8;
}

/// <summary>Kural kataloğunun saklandığı Azure Cosmos DB (NoSQL) ayarları.</summary>
public sealed class CatalogOptions
{
    /// <summary>Cosmos hesabının adresi (ör. https://hesap-adi.documents.azure.com:443/). Boşsa katalog kapalıdır.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// Cosmos anahtarı. Dosyaya yazılmaz; user-secrets ("Catalog:Key") veya Key Vault'tan gelir.
    /// Boşsa Microsoft Entra ID (DefaultAzureCredential) kullanılır.
    /// </summary>
    public string? Key { get; set; }

    public string Database { get; set; } = "codeanalyst";
    public string Container { get; set; } = "rules";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}

/// <summary>Ekrandaki "ihale tipi" seçimi. Kod haritasından kapsam (ilgili dosyalar) hesaplanır.</summary>
public sealed class ScopeDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Koddaki karşılığı (modele bağlam olarak verilir).</summary>
    public string CodeName { get; set; } = "";
    /// <summary>Kapsamın çekirdek sınıflarını seçen düzenli ifade (sınıf adı üzerinde).</summary>
    public string TypePattern { get; set; } = "";
    /// <summary>Kapsama dahil edilecek ekran dosyalarını seçen düzenli ifade (dosya yolu üzerinde).</summary>
    public string FrontendPattern { get; set; } = "";

    /// <summary>
    /// Sınıf adlarında bu ihale tipini gösteren anahtar kelime (ör. "AggregatedAuction", "Dutch").
    /// Konu kalıplarındaki "{Keyword}" bununla değiştirilir (ör. Generate{Keyword}ReportProcess).
    /// </summary>
    public string? Keyword { get; set; }
}

/// <summary>Ekrandaki "konu" seçimi. Akışın giriş noktalarını (controller metotları) bulmak için kullanılır.</summary>
public sealed class TopicDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Giriş noktası olan controller metotlarının adını seçen düzenli ifade (isteğe bağlı).</summary>
    public string? ActionPattern { get; set; }

    /// <summary>
    /// API adresi olmayan giriş noktaları için sınıf adı kalıbı (isteğe bağlı), ör. "^Generate{Keyword}ReportProcess$".
    /// "{Keyword}" seçili ihale tipinin anahtar kelimesiyle değiştirilir.
    /// </summary>
    public string? EntryTypePattern { get; set; }

    /// <summary>Başlangıç sınıflarındaki giriş metodu kalıbı (varsayılan: Run / Execute / Process / Handle).</summary>
    public string? EntryMethodPattern { get; set; }

    /// <summary>Odak klasörü: "repo/dosya/yolu" üzerinde düzenli ifade (ör. "^ProSourceCore/Promena\.Schedule/"). Aramalar önce burada yapılır.</summary>
    public string? FocusPathPattern { get; set; }

    /// <summary>Modele verilen kısa açıklama: bu konuda koda nereden bakılmalı.</summary>
    public string? Description { get; set; }
}
