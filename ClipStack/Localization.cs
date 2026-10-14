using System;
using System.Globalization;
using System.Windows.Markup;
using ClipStack.Resources;

namespace ClipStack
{
    public enum AppLanguage { System, Turkish, English }

    /// <summary>
    /// Arayüz dili. Metinler Resources\Strings.resx (Türkçe, varsayılan) ve Strings.en.resx'tedir.
    /// Dil uygulama başlarken bir kez seçilir; değiştirmek yeniden başlatma ister (pencereler, tepsi menüsü
    /// ve XAML metinleri açılışta bir kez yüklenir). Log mesajları bilinçli olarak her zaman Türkçedir.
    /// </summary>
    public static class Localization
    {
        private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
        private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

        /// <summary>Metinlerin dili (tr-TR veya en-US).</summary>
        public static CultureInfo UICulture { get; private set; } = TurkishCulture;

        /// <summary>
        /// Tarih ve sayı biçimi. Mümkünse Windows'un bölge ayarı kullanılır (ör. İngilizce arayüz + İngiltere
        /// biçimi); bölge ayarı seçilen dille çelişiyorsa (Türkçe Windows'ta İngilizce arayüz) dilin kendi biçimi.
        /// </summary>
        public static CultureInfo FormatCulture { get; private set; } = TurkishCulture;

        public static bool IsTurkish => UICulture.TwoLetterISOLanguageName == "tr";

        /// <summary>Dili uygular. Pencereler oluşmadan önce çağrılmalıdır.</summary>
        public static void Apply(AppLanguage language)
        {
            UICulture = ResolveUICulture(language, CultureInfo.InstalledUICulture);
            FormatCulture = ResolveFormatCulture(UICulture, CultureInfo.CurrentCulture);

            Strings.Culture = UICulture;
            CultureInfo.DefaultThreadCurrentUICulture = UICulture;
            CultureInfo.CurrentUICulture = UICulture;
            CultureInfo.DefaultThreadCurrentCulture = FormatCulture;
            CultureInfo.CurrentCulture = FormatCulture;
        }

        /// <summary>"Windows'a uy": Windows Türkçe ise Türkçe, değilse İngilizce (desteklenen tek diğer dil).</summary>
        internal static CultureInfo ResolveUICulture(AppLanguage language, CultureInfo windowsUICulture) => language switch
        {
            AppLanguage.Turkish => TurkishCulture,
            AppLanguage.English => EnglishCulture,
            _ => windowsUICulture.TwoLetterISOLanguageName == "tr" ? TurkishCulture : EnglishCulture
        };

        internal static CultureInfo ResolveFormatCulture(CultureInfo uiCulture, CultureInfo regionalCulture)
        {
            bool regionTurkish = regionalCulture.TwoLetterISOLanguageName == "tr";
            bool uiTurkish = uiCulture.TwoLetterISOLanguageName == "tr";
            if (regionTurkish == uiTurkish && !regionalCulture.Equals(CultureInfo.InvariantCulture))
                return regionalCulture;
            return uiTurkish ? TurkishCulture : EnglishCulture;
        }

        /// <summary>Kaynak metni, biçim kültürüyle doldurur: <c>L.F(Strings.Opt_Restored, count)</c>.</summary>
        public static string F(string format, params object?[] args) => string.Format(FormatCulture, format, args);

        /// <summary>Anahtarla metin (XAML uzantısı ve testler için). Bulunamazsa anahtarı köşeli parantezle döndürür.</summary>
        public static string Get(string key) => Strings.ResourceManager.GetString(key, UICulture) ?? $"[{key}]";
    }

    /// <summary>
    /// XAML'de metin: <c>Text="{l:Loc Tab_General}"</c>. Değer pencere yüklenirken bir kez okunur.
    /// Anahtarların varlığı derlemede değil testte (LocalizationTests) denetlenir.
    /// </summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension() { }
        public LocExtension(string key) => Key = key;

        [ConstructorArgument("key")]
        public string Key { get; set; } = "";

        public override object ProvideValue(IServiceProvider serviceProvider) => Localization.Get(Key);
    }
}
