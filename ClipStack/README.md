# ClipStack

Ditto tarzı, hafif bir Windows pano geçmişi uygulaması (C# / .NET 8 / WPF).
Metin (HTML/RTF biçimiyle), görsel ve kopyalanan dosyaları saklar.

## Gereksinimler
- Windows 10 veya 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- (İsteğe bağlı) Visual Studio 2022 veya JetBrains Rider

## Çalıştırma
```powershell
cd ClipStack
dotnet run
```
Uygulama sistem tepsisine yerleşir. Bir şeyler kopyala, sonra **Ctrl+Shift+V** ile geçmişi aç.
ClipStack zaten çalışırken exe'yi tekrar açmak, çalışan örneğin geçmiş penceresini açar.

## Tek exe olarak yayınlama
```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```
Çıktı: `bin\Release\net8.0-windows\win-x64\publish\ClipStack.exe`
(.NET yüklü olmayan makineler için `--self-contained true` kullan; dosya büyür.)
SQLite'ın yerel kütüphanesi (`e_sqlite3.dll`) csproj'daki `IncludeNativeLibrariesForSelfExtract` sayesinde exe'nin içine gömülür; tek dosyayı kopyalamak yeterli.

## Testler
```powershell
cd ..\ClipStack.Tests
dotnet test                      # birim testleri (geçici veritabanlarıyla)
$env:CLIPSTACK_IT="1"; dotnet test --filter IntegrationTests   # uçtan uca: ClipStack çalışırken
```
Uçtan uca testler gerçek panoyu kullanır (panodaki içerik değişir) ve gerçek geçmişe eklediği test kayıtlarını siler.

## Kısayollar (popup içinde)
| Tuş | İşlev |
|---|---|
| Yazmak | Anında arama (birden çok kelime, Türkçe büyük/küçük harf duyarsız) |
| ↑ ↓ / PgUp PgDn | Gezinme |
| Enter / çift tık | Önceki pencereye yapıştır (biçimiyle; dosya kayıtları dosya olarak) |
| Shift+Enter / Shift+çift tık | Düz metin olarak yapıştır (dosya kayıtlarında yollar) |
| Ctrl+Enter | Sadece panoya kopyala |
| Ctrl+1 … Ctrl+9 | Listedeki 1.–9. öğeyi hemen yapıştır |
| F3 | Önizleme panelini aç/kapat (tam metin, büyük görsel, dosya listesi) |
| Ctrl+P | Sabitle / sabitlemeyi kaldır |
| Shift+Del | Kaydı sil |
| Ctrl+Z | Son silmeyi geri al (son 10 silme) |
| Ctrl+O | Seçenekler |
| Esc veya kısayola tekrar basmak | Kapat |

Pencere açıkken yeni bir şey kopyalanırsa liste kendiliğinden yenilenir. Pencere kenarlarından boyutlandırılır, alttaki kısayol satırından sürüklenerek taşınır; boyutu (önizlemeli ve önizlemesiz ayrı ayrı) hatırlanır.

## Dosya yapısı
| Dosya | Görevi |
|---|---|
| `ClipboardMonitor.cs` | `AddClipboardFormatListener` + `RegisterHotKey`, parola yöneticisi formatlarını atlama |
| `ClipDatabase.cs` | SQLite saklama, arama, tekrar engelleme (SHA-256), sınırlar, geri alma, yedekleme |
| `PasteService.cs` | Panoya yazma (metin, görsel, dosya) ve `SendInput` ile Ctrl+V |
| `RawClipboard.cs` | HTML/RTF'yi Win32 ile ham bayt olarak okuma/yazma (Türkçe karakter güvenli) |
| `PopupWindow.xaml(.cs)` | Arama + liste + önizleme penceresi |
| `App.xaml.cs` | Başlangıç, tek örnek ve ikinci örnek sinyali, tepsi ikonu, genel hata yakalama, otomatik yedek |
| `ForegroundTracker.cs` | Son aktif uygulama penceresini izler (tepsiden açınca yapıştırma hedefi) |
| `Log.cs` | Hata günlüğü (`log.txt`) |
| `Settings.cs` | Kalıcı ayarlar (`settings.json`), sabit sınırlar, kısayol biçimlendirme |
| `StartupManager.cs` | Windows ile başlat (HKCU Run + Görev Yöneticisi StartupApproved) |
| `OptionsWindow.xaml(.cs)` | Seçenekler penceresi |
| `DatabaseKey.cs` | Veritabanı anahtarı: rastgele üretilir, Windows DPAPI ile hesaba bağlı saklanır |
| `SensitiveContent.cs` | Token/anahtar ve parolaya benzeyen metinleri tespit eder |
| `Assets/ClipStack.ico` | Uygulama, tepsi ve pencere ikonu (16–256 px). `IconGenerator.cs.txt` onu üreten kod |
| `ThemeManager.cs`, `Themes` | Açık/koyu tema: renkler (`Dark.xaml`, `Light.xaml`) ve temaya uyan kontrol şablonları (`Controls.xaml`) |
| `WindowPlacement.cs` | Pencereyi hedef monitörün fiziksel pikselleri ve DPI’ı ile konumlandırır |
| `ImageProcessing.cs` | Görseli arka planda hazırlar (PNG’ye çevirme, küçük resim, özet) |
| `app.manifest` | Per-Monitor V2 DPI bildirimi (farklı ölçekli monitörlerde keskin görüntü ve doğru konum) |
| `Localization.cs`, `Resources/Strings*.resx` | Arayüz dili: Türkçe (`Strings.resx`, varsayılan) ve İngilizce (`Strings.en.resx`). Log mesajları her zaman Türkçedir. |

Veriler: `%LOCALAPPDATA%\ClipStack\` içinde `history.db` (şifreli), `db.key` (DPAPI ile korunan anahtar), `settings.json`, `log.txt` ve `Backups\`.

## Seçenekler
Tepsi ikonuna sağ tık → **Seçenekler…** veya geçmiş penceresinde **Ctrl+O**. Değişiklikler "Uygula"ya basınca hemen etkili olur.
- **Genel:** en fazla kopya sayısı, bunlardan en fazla kaçının görsel olabileceği, kayıtları X gün sonra silme; görünüm (tema: Windows’a uy / açık / koyu; geçmiş penceresi: imlecin yanında / son konumunda / ekranın ortasında; dil: Windows’a uy / Türkçe / English — yeniden başlatınca geçerli olur); Windows ile başlat.
- **Kısayol ve Gizlilik:** geçmişi açan kısayol (kutuya tıklayıp yeni birleşime bas), kaydedilmeyecek uygulamalar (satır başına bir süreç adı).
- **İstatistikler:** toplam kopyalama/yapıştırma, kayıt sayıları, veritabanı boyutu; sayaç sıfırlama, sıkıştırma, geçmişi temizleme.
- **Yedekleme:** elle yedek alma (.db), yedekten geri yükleme (mevcut geçmişe ekler, hiçbir şey silmez), metinleri .txt olarak dışa aktarma, günlük otomatik yedek (son 7 tanesi saklanır).
- **Hakkında:** sürüm, kısayol, çalışma ortamı, veri klasörü.

Not: "Windows ile başlat" işaretlendiği anda çalışan exe'nin yolunu kaydeder. Kalıcı kullanım için önce `dotnet publish` ile exe'yi sabit bir klasöre koyup oradan açıp ayarı işaretle. Sonradan `dotnet run` ile başka bir kopyayı çalıştırmak bu kaydı değiştirmez; yol sadece kayıttaki exe silinmiş/taşınmışsa güncellenir. Windows açılışında (`--startup`) ClipStack zaten çalışıyorsa ikinci örnek sessizce kapanır.

Geçmiş penceresi en yeni 500 kaydı gösterir; daha eskileri aramayla bulunur.
Silinen kayıtların yeri veritabanı dosyasından otomatik geri verilir (SQLite artımlı `auto_vacuum`).

## Gizlilik ve güvenlik
- **Şifreli geçmiş:** `history.db` (ve WAL dosyası) SQLCipher ile şifrelenir. Anahtar rastgele üretilir ve Windows DPAPI ile sadece bu Windows hesabının çözebileceği şekilde `db.key` dosyasında saklanır; parola sorulmaz. Eski sürümden kalan şifresiz geçmiş ve otomatik yedekler ilk açılışta şifrelenir.
- **Anahtar kaybı:** Windows hesabı silinir/sıfırlanırsa veya `db.key` kaybolursa geçmiş açılamaz. ClipStack bu durumda eski dosyaları silmeden kenara alıp boş geçmişle başlamayı önerir.
- **Yedekler:** Otomatik yedekler şifrelidir. Elle yedek alırken şifreli (sadece bu hesapta açılır) veya şifresiz (başka bilgisayara taşınabilir ama dosyayı açan okuyabilir) seçilebilir. Metin dışa aktarma (.txt) her zaman şifresizdir.
- **Kaydedilmeyenler:** "Beni kaydetme" işareti koyan uygulamalar (parola yöneticilerinin çoğu); hariç tutulan uygulamalar listesi (varsayılan: yaygın parola yöneticileri); bilinen token/anahtar biçimleri (GitHub, GitLab, Slack, npm, AWS, Google, Stripe, `sk-…` API anahtarları, JWT, PEM özel anahtarlar, Azure bağlantı anahtarları); isteğe bağlı olarak parolaya benzeyen tek parça metinler. Atlanan içeriğin kendisi log'a yazılmaz, sadece türü yazılır.
- **Kopyalamayı duraklat:** Tepsi menüsü → Kopyalamayı duraklat → 5 dk / 15 dk / 1 saat / sürdürene kadar. Duraklatılmışken tepsi ikonunda turuncu işaret, geçmiş penceresinde uyarı görünür. Duraklatma, uygulama kapanınca sona erer.

## Bilinen sınırlamalar
- Şifrelemeden önce diskte duran eski şifresiz dosyanın silinen blokları diskte fiziksel olarak kalabilir (SSD/dosya sistemi davranışı); tamamen kapatmak için disk şifrelemesi (BitLocker) önerilir.
- Dosya kayıtları dosyaların kendisini değil yollarını saklar; dosya taşınır/silinirse yapıştırılamaz (önizlemede "artık yok" yazar).
- Geçmişten yapıştırılan dosyalar her zaman kopyalanır; Gezgin'de "kes" yapılmış olsalar bile taşınmaz.
- Yönetici olarak çalışan bir pencereye yapıştırmak için ClipStack'in de yönetici olarak çalışması gerekir (Windows UIPI kısıtı).

## Sonraki adımlar
1. Gruplar/etiketler, snippet şablonları
2. Kurulum paketi (MSIX veya Inno Setup)

## Çeviri / Translations
Arayüz metinleri `ClipStack\Resources\Strings.resx` (Türkçe) ve `Strings.en.resx` (İngilizce) dosyalarındadır.
Kodda `Strings.Anahtar`, XAML'de `{l:Loc Anahtar}` ile kullanılır; `Strings` sınıfı derleme sırasında üretilir.
Yeni dil eklemek için `Strings.<dil>.resx` (ör. `Strings.de.resx`) oluşturup `Localization.cs` içindeki dil listesine ekleyin.
Testler iki dosyanın aynı anahtarlara ve aynı `{0}` yer tutucularına sahip olduğunu, XAML'deki her anahtarın var olduğunu
ve kullanılmayan metin kalmadığını denetler.
