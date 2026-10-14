# ClipStack

Windows için hafif, şifreli bir pano geçmişi yöneticisi. Kopyaladığınız metin, görsel ve dosyaları saklar;
**Ctrl+Shift+V** ile arayıp istediğiniz yere yeniden yapıştırmanızı sağlar. [Ditto](https://ditto-cp.sourceforge.io/)'dan esinlenilmiştir.

*English summary below.*

## Özellikler

- **Metin, görsel ve dosya** geçmişi; HTML/RTF biçimiyle (Word, Outlook, tarayıcı) ya da düz metin olarak yapıştırma
- **Anında arama** (Türkçe karakterlere duyarlı), sabitleme, Ctrl+1…9 ile hızlı yapıştırma, önizleme paneli (F3)
- **Şifreli geçmiş:** veritabanı SQLCipher ile şifrelenir; anahtar Windows hesabınıza bağlıdır (DPAPI)
- **Gizlilik:** parola yöneticilerinden kopyalananları, bilinen anahtar/token biçimlerini (GitHub, AWS, JWT…) kaydetmez; "Kopyalamayı duraklat" ile geçici olarak kapatılabilir
- **Yedekleme:** elle ya da günlük otomatik yedek, yedekten geri yükleme, metinleri .txt olarak dışa aktarma
- Açık/koyu tema, Türkçe ve İngilizce arayüz, çoklu monitör ve yüksek DPI desteği

## Gereksinimler

- Windows 10 (1607+) veya Windows 11, x64
- Kaynaktan derlemek için [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Derleme ve çalıştırma

```powershell
cd ClipStack
dotnet run
```

Uygulama sistem tepsisine yerleşir. Bir şeyler kopyalayıp **Ctrl+Shift+V**'ye basın.

Dağıtılabilir tek exe (.NET gömülü) ve kurulum paketi için depo kökünde:

```powershell
.\build.ps1
```

Kurulum paketi için [Inno Setup 6](https://jrsoftware.org/isdl.php) gerekir; yoksa sadece `artifacts\publish\ClipStack.exe` üretilir.

Kısayollar, seçenekler, dosya yapısı ve güvenlik ayrıntıları: **[ClipStack/README.md](ClipStack/README.md)**

## Lisans

ClipStack özgür yazılımdır: [GNU Genel Kamu Lisansı v3.0](LICENSE) veya sonraki bir sürümün koşullarıyla
dağıtabilir ve değiştirebilirsiniz. Hiçbir garanti olmadan sunulur.
Kullanılan üçüncü taraf bileşenlerin lisansları: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)

---

## English summary

ClipStack is a lightweight, encrypted clipboard history manager for Windows. It keeps the text, images and files
you copy and lets you search and paste them again with **Ctrl+Shift+V**.

- Text, image and file history; paste with formatting (HTML/RTF) or as plain text
- Instant search, pinning, Ctrl+1…9 quick paste, preview pane (F3)
- Encrypted history (SQLCipher, key protected with Windows DPAPI)
- Skips password managers and known secret/token formats; "Pause copying" from the tray menu
- Manual and daily automatic backups, restore, export texts to .txt
- Light/dark theme, Turkish and English UI, multi-monitor and high-DPI aware

**Build:** Windows 10/11 x64 and the .NET 8 SDK. Run `dotnet run` in `ClipStack\`, or `.\build.ps1` at the
repository root for a self-contained single exe (and an installer if Inno Setup 6 is installed).

**License:** [GPL-3.0-or-later](LICENSE). Third-party notices: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
