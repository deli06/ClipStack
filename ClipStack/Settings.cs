using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;

namespace ClipStack
{
    /// <summary>Kullanıcının Seçenekler penceresinden değiştirebildiği ayarlar (settings.json).</summary>
    public sealed class AppSettings
    {
        /// <summary>Sabitlenmemiş öğeler için tutulacak en fazla kayıt.</summary>
        public int MaxHistory { get; set; } = 500;

        /// <summary>Açıksa, son kullanımından bu kadar gün geçen sabitlenmemiş kayıtlar silinir.</summary>
        public bool ExpireEnabled { get; set; } = false;
        public int ExpireDays { get; set; } = 30;

        /// <summary>Sabitlenmemiş görseller için ayrı sınır (görseller yer kaplar).</summary>
        public int MaxImages { get; set; } = 100;

        /// <summary>Geçmiş penceresini açan global kısayol (MOD_* bayrakları + sanal tuş kodu).</summary>
        public uint HotkeyModifiers { get; set; } = Settings.DefaultHotkeyModifiers;
        public uint HotkeyKey { get; set; } = Settings.DefaultHotkeyKey;

        /// <summary>Bu süreçlerden gelen kopyalamalar kaydedilmez (süreç adı, .exe olmadan).</summary>
        public List<string> ExcludedApps { get; set; } = new(Settings.DefaultExcludedApps);

        /// <summary>Geçmiş penceresinde önizleme paneli açık mı (F3).</summary>
        public bool ShowPreview { get; set; } = false;

        /// <summary>Günde bir kez Backups klasörüne otomatik yedek alınsın mı.</summary>
        public bool AutoBackup { get; set; } = false;

        /// <summary>Bilinen anahtar/token biçimlerini (GitHub, AWS, JWT, özel anahtar…) içeren metinler kaydedilmesin.</summary>
        public bool SkipSecrets { get; set; } = true;

        /// <summary>Parolaya benzeyen tek parça metinler kaydedilmesin (yanlış alarm verebilir, varsayılan kapalı).</summary>
        public bool SkipPasswordLike { get; set; } = false;

        /// <summary>Renk teması: Windows ayarını izle, her zaman açık veya her zaman koyu.</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
        public AppTheme Theme { get; set; } = AppTheme.System;

        /// <summary>Geçmiş penceresi nerede açılsın.</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
        public PopupPlacement PopupPlacement { get; set; } = PopupPlacement.Cursor;

        /// <summary>Geçmiş penceresinin boyutu (DIP). Önizleme açıkken ayrı genişlik tutulur.</summary>
        public double PopupWidth { get; set; } = Settings.DefaultPopupWidth;
        public double PopupWidthWithPreview { get; set; } = Settings.DefaultPopupWidthWithPreview;
        public double PopupHeight { get; set; } = Settings.DefaultPopupHeight;

        /// <summary>"Son konumunda" için pencerenin sol üst köşesi (fiziksel piksel, sanal ekran koordinatı).</summary>
        public int? PopupLeftPx { get; set; }
        public int? PopupTopPx { get; set; }

        /// <summary>
        /// Hangi sürümün varsayılanlarının uygulandığı. Yeni sürüm varsayılan listeye uygulama eklediğinde,
        /// eski settings.json'daki listeye de eklenebilsin diye. Dosyada yoksa 0 okunur.
        /// </summary>
        public int DefaultsVersion { get; set; }
    }

    public enum AppTheme { System, Light, Dark }

    public enum PopupPlacement { Cursor, Remember, Center }

    public static class Settings
    {
        public const double DefaultPopupWidth = 560;
        public const double DefaultPopupWidthWithPreview = 988;
        public const double DefaultPopupHeight = 460;
        public const double MinPopupWidth = 380;
        public const double MinPopupHeight = 280;
        public const double MaxPopupSize = 6000;

        public const int MinHistory = 10;
        public const int MaxHistoryLimit = 100_000;
        public const int MinExpireDays = 1;
        public const int MaxExpireDays = 3650;
        public const int MinImages = 1;
        public const int MaxImagesLimit = 1000;

        /// <summary>Bundan uzun metinler kırpılır (veritabanı şişmesin diye).</summary>
        public const int MaxTextLength = 200_000;

        /// <summary>Bundan büyük HTML/RTF biçim verisi saklanmaz (metnin kendisi yine kaydedilir).</summary>
        public const int MaxFormatBytes = 4 * 1024 * 1024;

        /// <summary>
        /// Geçmiş penceresinde gösterilen en fazla kayıt. Daha eskiler aramayla bulunur;
        /// tüm geçmişi her açılışta listeye doldurmak büyük geçmişlerde pencereyi yavaşlatır.
        /// </summary>
        public const int MaxListItems = 500;

        /// <summary>Bundan büyük görseller (PNG olarak) kaydedilmez.</summary>
        public const int MaxImageBytes = 25 * 1024 * 1024;

        /// <summary>Otomatik yedeklerden en fazla bu kadarı saklanır; eskiler silinir.</summary>
        public const int AutoBackupKeep = 7;

        /// <summary>Varsayılan kısayol: Ctrl + Shift + V</summary>
        public const uint DefaultHotkeyModifiers = Native.MOD_CONTROL | Native.MOD_SHIFT;
        public const uint DefaultHotkeyKey = Native.VK_V;

        /// <summary>
        /// Varsayılan olarak kaydedilmeyen parola yöneticileri (süreç adları). Çoğu panoya zaten
        /// "beni kaydetme" işareti koyar; bu liste koymayan sürümlere karşı ikinci bir güvencedir.
        /// </summary>
        public static readonly string[] DefaultExcludedApps =
        {
            "KeePass", "KeePassXC", "KeeWeb", "1Password", "Bitwarden", "Dashlane", "LastPass",
            "Enpass", "NordPass", "RoboForm", "Keeper", "pwsafe", "ProtonPass", "Proton Pass",
        };

        /// <summary><see cref="AppSettings.DefaultsVersion"/> için güncel değer.</summary>
        public const int CurrentDefaultsVersion = 2;

        public static AppSettings Current { get; private set; } = new();

        public static string HotkeyText => FormatHotkey(Current.HotkeyModifiers, Current.HotkeyKey);

        /// <summary>Kaynak uygulama hariç tutulanlar listesinde mi (büyük/küçük harf duyarsız).</summary>
        public static bool IsExcludedApp(string? app) =>
            app != null && Current.ExcludedApps.Any(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase));

        public static string DataFolder
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipStack");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string DatabasePath => Path.Combine(DataFolder, "history.db");
        public static string SettingsPath => Path.Combine(DataFolder, "settings.json");
        public static string BackupFolder => Path.Combine(DataFolder, "Backups");

        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
            catch
            {
                // Bozuk dosya: varsayılanlarla devam et.
                Current = new AppSettings();
            }
            Clamp(Current);
        }

        /// <summary>Önce geçici dosyaya yazar, sonra yer değiştirir; yazma yarıda kalırsa ayarlar bozulmaz.</summary>
        public static void Save()
        {
            Clamp(Current);
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsPath, overwrite: true);
        }

        internal static void Clamp(AppSettings s)
        {
            s.MaxHistory = Math.Clamp(s.MaxHistory, MinHistory, MaxHistoryLimit);
            s.ExpireDays = Math.Clamp(s.ExpireDays, MinExpireDays, MaxExpireDays);
            s.MaxImages = Math.Clamp(s.MaxImages, MinImages, MaxImagesLimit);
            s.PopupWidth = ClampSize(s.PopupWidth, MinPopupWidth, DefaultPopupWidth);
            s.PopupWidthWithPreview = ClampSize(s.PopupWidthWithPreview, MinPopupWidth, DefaultPopupWidthWithPreview);
            s.PopupHeight = ClampSize(s.PopupHeight, MinPopupHeight, DefaultPopupHeight);
            if (!Enum.IsDefined(s.Theme)) s.Theme = AppTheme.System;
            if (!Enum.IsDefined(s.PopupPlacement)) s.PopupPlacement = PopupPlacement.Cursor;
            if (!IsValidHotkey(s.HotkeyModifiers, s.HotkeyKey))
            {
                s.HotkeyModifiers = DefaultHotkeyModifiers;
                s.HotkeyKey = DefaultHotkeyKey;
            }
            s.ExcludedApps = NormalizeAppList(s.ExcludedApps ?? new List<string>());

            // Eski ayar dosyası: bu sürümde eklenen varsayılan parola yöneticilerini de listeye kat.
            // (Kullanıcının sildiği eski varsayılanlar geri gelmez; sadece yenileri eklenir.)
            if (s.DefaultsVersion < CurrentDefaultsVersion)
            {
                s.ExcludedApps = NormalizeAppList(s.ExcludedApps.Concat(DefaultExcludedApps.Except(
                    new[] { "KeePass", "KeePassXC", "1Password", "Bitwarden" }, StringComparer.OrdinalIgnoreCase)));
                s.DefaultsVersion = CurrentDefaultsVersion;
            }
        }

        private static double ClampSize(double value, double min, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, MaxPopupSize) : fallback;

        /// <summary>Boşları atar, ".exe" uzantısını kırpar, tekrarları birleştirir.</summary>
        internal static List<string> NormalizeAppList(IEnumerable<string> apps) =>
            apps.Select(a => a.Trim())
                .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4].TrimEnd() : a)
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// Kısayolda Ctrl veya Alt olmalı; yoksa (ör. sadece Shift+V) normal yazmayı engeller.
        /// Tuş da bir değiştirici (Ctrl, Shift…) olmamalı.
        /// </summary>
        internal static bool IsValidHotkey(uint modifiers, uint vk)
        {
            if ((modifiers & (Native.MOD_CONTROL | Native.MOD_ALT)) == 0) return false;
            if (vk == 0 || vk > 0xFE) return false;
            Key key = KeyInterop.KeyFromVirtualKey((int)vk);
            return key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System);
        }

        /// <summary>Kısayolu "Ctrl+Shift+V" biçiminde yazar.</summary>
        internal static string FormatHotkey(uint modifiers, uint vk)
        {
            string mods = FormatModifiers(modifiers);
            string key = KeyName(KeyInterop.KeyFromVirtualKey((int)vk));
            return mods.Length == 0 ? key : mods + "+" + key;
        }

        /// <summary>Sadece değiştirici tuşları "Ctrl+Shift" biçiminde yazar.</summary>
        internal static string FormatModifiers(uint modifiers)
        {
            var parts = new List<string>();
            if ((modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
            return string.Join("+", parts);
        }

        private static string KeyName(Key key) => key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
            Key.Oem3 => "\"",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Space => "Boşluk",
            Key.Insert => "Ins",
            Key.Return => "Enter",
            _ => key.ToString()
        };
    }
}
