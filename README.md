# Promena Rehberi (pilot)

> Kod içindeki proje adları (`KodAnalist`, `KodHaritasi`) ve klasör adları değişmedi; yalnızca ürünün görünen adı Promena Rehberi'dir.

Analistlerin iş kurallarıyla ilgili sorularını, üç reponun (promena_frontend, ProSourceCore, Sourcing)
kaynak kodunu inceleyerek cevaplayan uygulama. Cevaplar Microsoft Foundry üzerindeki Claude modeliyle üretilir;
analiste sade dille ve örneklerle anlatılır, kod referansları ekranda kapalı bir "Teknik dayanaklar" bölümünde durur.

## Nasıl çalışır?

1. Analist ihale tipini, konuyu ve cevap modunu (Hızlı / Dengeli / Derin) seçip soruyu yazar. Aynı soru ya da
   çok benzer bir soru kural kataloğunda onaylıysa cevap **model çağrılmadan** gelir.
2. Değilse Claude, kod haritası ve salt okunur araçlarla kodu inceler (dosya yazamaz, komut çalıştıramaz).
3. Taslak cevap, program tarafından hazırlanan kanıt paketiyle ayrıca doğrulanır (Hızlı modda atlanır).
4. Analist cevabı kontrol edip onaylarsa cevap kataloğa kaydedilir (her modda; doğrulanmamışsa öyle işaretlenir);
   dayandığı kod değişirse "kod değişti" olarak işaretlenir.

## Bağlantı kopması ve sayfa yenileme

İnceleme sunucuda tarayıcı bağlantısından bağımsız çalışır (`AskJobs.cs`):
- Bağlantı koparsa (Wi‑Fi, VPN, proxy) ekran aynı incelemeye kendiliğinden yeniden bağlanır ve kaldığı yerden devam eder.
- Sayfa yenilenirse son inceleme (süren ya da biten) geri gelir. Biten incelemeler 60 dakika saklanır.
- Olay gelmezken sunucu 15 saniyede bir boş bir satır gönderir; ağ cihazları ve Azure bağlantıyı "boşta" sayıp kesmez.
- Hiçbir sayfanın bağlı olmadığı inceleme 5 dakika sonra durdurulur (boşuna model maliyeti oluşmaz).
- Hata mesajı gelmiş taslağı veya cevabı silmez; ayrı bir uyarı olarak gösterilir. Doğrulama aşaması hata verirse taslak
  "doğrulanmamış" cevap olarak gösterilir.
- Süren incelemeler bellekte tutulur: sunucu yeniden başlarsa kaybolur (ekran bunu bildirir).

Kopan bir incelemeye bağlanmak için: `GET /api/ask/{id}/stream?after={sıra}`.

## Cevap modları

Katalogda cevap yoksa modelin ne kadar titiz (ve ne kadar uzun) çalışacağını analist seçer. Seçim tarayıcıda hatırlanır.

| Mod | Düşünme | 1. aşama araç turu | Doğrulama | Hedef süre |
|---|---|---|---|---|
| **Hızlı** | low | 10 | yok (cevap "doğrulanmamış" işaretlenir) | 1–2 dk |
| **Dengeli** (varsayılan) | medium | 20 | kanıt paketiyle tek çağrı, ek okuma yok | 3–4 dk |
| **Derin** | high | 40 | kanıt paketi + en fazla 8 ek okuma | 8–10 dk |

Süreler hedeftir; gerçek süre sorunun karmaşıklığına ve modele bağlıdır. Her cevabın altında mod, süre ve token özeti
görünür; ayarları bu değerlere bakarak `appsettings.json` → `AnswerModes` bölümünden değiştirebilirsiniz:

- `Effort`: `low`, `medium`, `high`, `xhigh`, `max`. Düşük seviye daha hızlıdır.
- `MaxToolTurns`: 1. aşamadaki en fazla araç turu (her tur modele ayrı bir gidiş-dönüştür).
- `Verify`: `none`, `single` veya `tools`; `VerifyMaxToolTurns` yalnızca `tools` ile kullanılır.
- `Deployment` (isteğe bağlı): bu mod için Foundry'de farklı bir model dağıtımı (ör. Hızlı mod için daha hızlı bir model).
- `Default`: ekranda varsayılan seçili mod.

## Yeni bir bilgisayarda kurulum

### 1. Gerekenler

- **.NET 8 SDK** (`dotnet --version` ile kontrol edin).
- Üç repo yerel olarak klonlanmış olmalı: `promena_frontend`, `ProSourceCore`, `Sourcing`.
- Azure'da: Claude modeli dağıtılmış bir **Microsoft Foundry** kaynağı ve (katalog için) bir **Cosmos DB for NoSQL** hesabı.
  Bilgisayarın ağından bu iki kaynağa erişim olmalı (Cosmos'ta "Selected networks" açıksa bu bilgisayarın IP'si eklenmeli).

### 2. Klasör düzeni

Varsayılan olarak repolar `kod-analist` klasörünün **yanında** aranır:

```
Projeler/
├── kod-analist/
├── promena_frontend/
├── ProSourceCore/
└── Sourcing/
```

Repolarınız başka yerdeyse 3. adımdaki dosyada yollarını yazmanız yeterli.

### 3. Makineye özel ayarlar

`src/KodAnalist/appsettings.Local.example.json` dosyasını aynı klasöre **`appsettings.Local.json`** adıyla kopyalayıp doldurun:

- `Foundry:ResourceName` ve `Foundry:Deployment`
- `Catalog:Endpoint` (boş bırakılırsa katalog kapalı çalışır)
- `Repos` (yalnızca repolar `kod-analist`'in yanında değilse; sıra `appsettings.json` ile aynı olmalı)

Bu dosya Git'e gönderilmez. Hem uygulama hem kod haritası programı bu dosyayı okur.

### 4. Anahtarlar (user-secrets)

Anahtarlar hiçbir dosyaya yazılmaz; .NET user-secrets deposunda (proje klasörünün dışında) tutulur.
**Komut geçmişine düşmemesi için** anahtarı komut satırına yazmak yerine `dotnet user-secrets` dosyasını bir
editörle açıp yapıştırmanız önerilir:

```bash
cd src/KodAnalist
dotnet user-secrets set "Foundry:ApiKey" "<yer-tutucu>"
dotnet user-secrets set "Catalog:Key" "<yer-tutucu>"
```

Ardından secrets dosyasını açıp yer tutucuları gerçek anahtarlarla değiştirin
(macOS/Linux: `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json`,
Windows: `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`; `UserSecretsId` değeri `KodAnalist.csproj` içindedir).

`Foundry:ApiKey` boş bırakılırsa Microsoft Entra ID (`DefaultAzureCredential`, ör. `az login`) kullanılır.

### 5. Kod haritasını üretin ve çalıştırın

```bash
cd src/KodHaritasi
dotnet run -- build          # ~20 sn; repolar güncellendikçe tekrar çalıştırın
cd ../KodAnalist
dotnet run
```

Tarayıcıda `http://localhost:5062` adresini açın.

## Repoların otomatik güncellenmesi (RepoSync)

Açıldığında uygulama, Azure DevOps'taki repoları **kendine ait ayrı bir klasöre** (`kod-analist/repos/`, Git'e
gönderilmez) klonlar ve belirli aralıklarla izlenen dalın son halini çeker. Kod değiştiyse kod haritasını kendisi
yeniden üretir; böylece kural kataloğundaki "güncel / kod değişti" durumu da kendiliğinden güncellenir. Ekranın üstünde
hangi dalın hangi commit'inin kullanıldığı ve son kontrol zamanı görünür; "Şimdi güncelle" düğmesiyle beklemeden güncellenir.

- **Salt okunurdur: hiçbir dala commit atmaz, push yapmaz.** Uygulama yalnızca okuma amaçlı git komutlarını
  çalıştırabilir (klonlama, çekme, sürüm okuma); klonlarda push adresi geçersiz bir değere ayarlanarak push teknik
  olarak da engellenir.
- **Sizin çalışma klasörünüze dokunmaz.** Uygulama her zaman uzak daldaki (ör. `development`) kodu görür, sizin
  yerel olarak üzerinde çalıştığınız değişiklikleri görmez.
- Git, bilgisayardaki kayıtlı kimlik bilgisini kullanır (Windows/macOS'ta Git Credential Manager). Bu bilgisayarda
  `git clone` ile Azure DevOps'tan repo çekebiliyorsanız ek ayar gerekmez. Adrese PAT gömmeyin.
- İlk açılışta klonlama repoların boyutuna göre birkaç dakika sürebilir; bu sırada ekranda "repolar hazırlanıyor" görünür.

Açmak için `appsettings.Local.json` içinde (örnek: `appsettings.Local.example.json`, "SEÇENEK B"):

```json
"RepoSync": { "Enabled": true, "Branch": "development", "IntervalMinutes": 15 },
"Repos": [
  { "Name": "promena_frontend", "RemoteUrl": "https://dev.azure.com/<kurum>/<proje>/_git/promena_frontend" },
  { "Name": "ProSourceCore",    "RemoteUrl": "https://dev.azure.com/<kurum>/<proje>/_git/ProSourceCore" },
  { "Name": "Sourcing",         "RemoteUrl": "https://dev.azure.com/<kurum>/<proje>/_git/Sourcing" }
]
```

Durumu model çağırmadan görmek için: `GET /api/sync-status`.

## Kod haritası (KodHaritasi)

`src/KodHaritasi`, model kullanmadan (API anahtarı ve token gerektirmeden) üç reponun çağrı haritasını çıkarır:
ekran → BFF adresi → BFF metotları → (HTTP) Sourcing adresi → servis metotları. Ayrıca MediatR
(`_mediator.Send(new XRequest())` → handler) ve arayüz → uygulayan sınıf bağlantılarını kurar.

Repo listesi `src/KodAnalist/appsettings.json` (ve varsa `appsettings.Local.json`) içindeki `Repos` bölümünden okunur.
Harita `data/codemap.json` dosyasına yazılır (Git'e gönderilmez; her bilgisayarda yeniden üretilir).

```bash
cd src/KodHaritasi
dotnet run -- build                                                    # haritayı üret (~20 sn)
dotnet run -- trace "/api/AggregatedAuctionSupplier/SendBid" --derinlik 4   # akışı aşağı doğru izle
dotnet run -- callers "AggregatedAuctionSupplierController.ValidateBidSettings"  # kim çağırıyor (ekrana kadar)
dotnet run -- find "AggregatedAuction"                                 # adres / metot / tip ara
dotnet run -- stats                                                    # özet
```

`--tumu` ekleyerek gizlenen altyapı çağrılarını (hata mesajı ekleme vb.) da görebilirsiniz.
Kod değiştikten sonra (ör. `git pull` sonrası) `build` komutunu tekrar çalıştırın.

**Bilinen sınırlar** (derleme yapılmadan, sözdizimi ağacıyla çalıştığı için):
- `var x = Hesapla()` gibi tipi yazılmamış yerel değişkenler üzerinden yapılan çağrılar çözülemeyebilir.
- Extension metotlar ve lambda içindeki dolaylı çağrılar kısmen kaçabilir.
- Bir arayüzün birden fazla uygulaması varsa hepsi "olası hedef" olarak listelenir.
- Ekran tarafında yalnızca `/api/...` adresleri (düz metin veya aynı dosyadaki sabit) bulunur.
- İhale tipine göre çalışma anında seçilen adreslerde (ör. `GetAuctionActionUrl`) her tip ayrı hedef olarak listelenir.

## İhale tipi ve konu seçimi

Ekranda iki seçim var (liste `appsettings.json` içindeki `Scopes` ve `Topics` bölümlerinden gelir):

- **İhale tipi:** Seçilen tipin çekirdek sınıfları (ör. `AggregatedAuction*`), türedikleri taban sınıflar,
  çağırdıkları metotlar ve ilgili ekranlar kod haritasından hesaplanır. Aramalar önce bu kapsamda yapılır;
  bulunamazsa otomatik olarak tüm repolarda aranır. Diğer ihale tiplerine ait sınıflar kapsam dışında kalır.
- **Konu:** Akışın giriş noktaları (ör. teklif vermede `POST /api/AggregatedAuctionSupplier/SendBid`) bulunur
  ve modele "buradan başla" diye verilir.

Kapsamı model çağırmadan görmek için: `GET /api/scope-preview?scope=aggregated&topic=send-bid`

Konu tanımı (`Topics`) şu alanları alabilir; hepsi isteğe bağlıdır:

| Alan | Anlamı |
|---|---|
| `ActionPattern` | Giriş noktası olan controller metotları (seçili ihale tipinin controller'larında aranır), ör. `^SendBid$` |
| `EntryTypePattern` | API adresi olmayan giriş noktaları için sınıf kalıbı; `{Keyword}` ihale tipinin `Keyword` değeriyle değiştirilir, ör. `^Generate{Keyword}ReportProcess$` |
| `EntryMethodPattern` | Bu sınıflardaki giriş metodu (varsayılan `Run/Execute/Process/Handle`) |
| `FocusPathPattern` | Odak klasörü (`repo/yol` üzerinde); aramalar önce burada yapılır, ör. `^ProSourceCore/Promena\.Schedule/` |
| `Description` | Modele, bu konuda koda nereden bakılacağını anlatan kısa açıklama |

Örnekler: **Süreç raporu** BFF'deki `GetAuctionReport` ve Schedule'daki `Generate…ReportProcess.Run` noktalarından başlar,
aramayı `Promena.Schedule`'a odaklar. **Orta katman** aramayı `ProSource.API`'ye odaklar; ihale tipi seçiliyse o tipe ait
ProSource.API adreslerini giriş noktası yapar.

## Başlangıç kod paketi (1. aşama)

Konu seçildiğinde akışın giriş noktası bellidir. Program, model başlamadan bu noktadan itibaren akış ağacını,
akıştaki kontrol metotlarını (ekran, BFF, Sourcing) ve yer kalırsa bunların yardımcı metotlarını keser ve
soruyla birlikte modele verir. Model kodu parça parça aramak/okumak yerine bu paketle başlar; yalnızca pakette
olmayan yerleri (ör. enum ve ayar sabitleri) okur. Konu seçilmezse paket oluşmaz.

Paketi model çağırmadan görmek için: `GET /api/starter-preview?scope=aggregated&topic=send-bid`

## Doğrulama aşaması ve kanıt paketi

Taslak cevap hazırlandıktan sonra program, **model kullanmadan** bir kanıt paketi hazırlar: taslaktaki
`dosya:satır` referansları, adı geçen metotların gövdeleri, akış ağacı ve akıştaki kontrol metotları
(ekran, BFF, Sourcing). Ayrı bir doğrulayıcı bu paketle çalışır. Derin modda en fazla `VerifyMaxToolTurns`
(varsayılan 8) ek okuma yapabilir; Dengeli modda ek okuma yapmadan tek çağrıda doğrular; Hızlı modda bu aşama atlanır. Kanıt paketini görmek için: `POST /api/evidence-preview` (`{ "draft": "...", "scopeId": "...", "topicId": "..." }`).

## Kural kataloğu (Azure Cosmos DB)

Analistin onayladığı cevaplar Cosmos DB'ye kaydedilir. Aynı ihale tipi / konu seçildiğinde onaylı kurallar listelenir
ve tıklanınca **model çağrılmadan** açılır.

**"Sor"a basıldığında önce katalog:** Soru, aynı ihale tipindeki onaylı kayıtlarla model kullanmadan karşılaştırılır.
Benzerlik hem soru metnine hem onaylı cevabın analist bölümüne bakılarak hesaplanır (Türkçe ekler için kelime kökü yaklaşığı).
- Birebir aynı soru → onaylı cevap doğrudan açılır.
- Benzerliği `Catalog:AutoOpenThreshold` (varsayılan 0,75) üstünde ve kodu değişmemiş kayıt → doğrudan açılır; üstte
  eşleşen soru, benzerlik oranı, diğer benzer kurallar ve "Bu değil, modele sor" düğmesi görünür.
- %50 üstü benzerlikteki diğer kayıtlar → öneri listesi.

Hızlı modda üretilip onaylanan cevaplar katalogda **"doğrulanmamış"** olarak görünür.

- Her kayıt: soru, ihale tipi, konu, cevap, not, onaylayan, tarih, cevabın üretildiği mod, doğrulanıp doğrulanmadığı
  ve cevabın dayandığı metotlar.
- **Kod değişikliği takibi:** Dayanaklar metot düzeyinde izlenir (içeriğin SHA-256 parmak izi). Dosyanın başka bir yerine
  kod eklenip satırlar kaysa bile kayıt "değişti" görünmez; yalnızca kuralın dayandığı metot gerçekten değişirse
  **"kod değişti"** olarak işaretlenir ve hangi metodun değiştiği gösterilir. Bu kontrol model kullanmaz.
  Güncel sonuç için kod haritasını (`KodHaritasi build`) repolar güncellendikten sonra yeniden üretin.

### Kurulum

1. Azure Portal'da **Azure Cosmos DB for NoSQL** hesabı oluşturun (pilot için "Serverless" kapasite modu yeterli).
2. `src/KodAnalist/appsettings.json` → `Catalog:Endpoint` alanına hesabın adresini yazın
   (ör. `https://hesap-adi.documents.azure.com:443/`).
3. Kimlik doğrulama (ikisinden biri):
   - **Anahtar:** Portal → Keys → PRIMARY KEY değerini kopyalayıp dosyaya değil user-secrets'a kaydedin:
     `dotnet user-secrets set "Catalog:Key" "<anahtar>"`. Veritabanı (`codeanalyst`) ve kapsayıcı (`rules`,
     bölümleme anahtarı `/scopeId`) ilk kullanımda otomatik oluşturulur. Adlar `Catalog:Database` ve
     `Catalog:Container` ayarlarından değiştirilebilir.
   - **Microsoft Entra ID:** Anahtar boş bırakılırsa `DefaultAzureCredential` kullanılır. Bu durumda veritabanını ve
     kapsayıcıyı Portal'dan önceden oluşturun ve kullanıcıya "Cosmos DB Built-in Data Contributor" rolünü verin.

Katalog yapılandırılmamışsa uygulama katalogsuz çalışmaya devam eder.

Kontrol amaçlı (model ve Cosmos kullanmadan): `POST /api/catalog/sources-preview` (`{ "answer": "...", "scopeId": "aggregated" }`)
bir cevaptan hangi dayanakların izleneceğini gösterir.

## Sınırlar ve bilinen konular

- Pilot sürümde giriş/yetkilendirme yok; yalnızca iç ağda çalıştırın.
- Her soru bağımsızdır. Önceki soru bağlamı (sohbet geçmişi) tutulmuyor.
- Repolar otomatik güncellenmiyor. Güncel cevap için repolarda düzenli `git pull` yapılmalı.
- Bir soru en fazla 40 araç adımında tamamlanır (`Agent:MaxToolTurns`).
- Model nadiren de olsa yanlış çıkarım yapabilir. Kaynak referansları bu yüzden var: kritik kararlar öncesi kontrol edin.
