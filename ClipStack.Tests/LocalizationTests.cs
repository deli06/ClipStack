using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClipStack.Resources;
using Xunit;

namespace ClipStack.Tests
{
    [Collection("Settings")]
    public sealed class LocalizationTests : IDisposable
    {
        private static readonly string SourceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\ClipStack"));
        private static readonly string ResourceDir = Path.Combine(SourceDir, "Resources");

        public void Dispose() => Localization.Apply(AppLanguage.Turkish); // diğer testler Türkçe bekler

        private static Dictionary<string, string> ReadResx(string file) =>
            XDocument.Load(Path.Combine(ResourceDir, file)).Root!.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

        private static string[] Placeholders(string s) =>
            Regex.Matches(s, @"\{(\d+)(?:[,:][^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();

        [Fact]
        public void TurkishAndEnglish_HaveTheSameKeys_AndMatchingPlaceholders()
        {
            var tr = ReadResx("Strings.resx");
            var en = ReadResx("Strings.en.resx");

            Assert.Empty(tr.Keys.Except(en.Keys));   // İngilizcesi eksik metin
            Assert.Empty(en.Keys.Except(tr.Keys));   // Türkçesi olmayan fazla metin
            foreach (var key in tr.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(tr[key]), $"{key}: Türkçe boş");
                Assert.False(string.IsNullOrWhiteSpace(en[key]), $"{key}: İngilizce boş");
                Assert.True(Placeholders(tr[key]).SequenceEqual(Placeholders(en[key])),
                    $"{key}: yer tutucular farklı ({string.Join(",", Placeholders(tr[key]))} ≠ {string.Join(",", Placeholders(en[key]))})");
            }
        }

        [Fact]
        public void EnglishTexts_ContainNoTurkishCharacters()
        {
            var leftovers = ReadResx("Strings.en.resx").Where(kv => kv.Value.IndexOfAny("çÇğĞıİöÖşŞüÜ".ToCharArray()) >= 0)
                                                      .Select(kv => kv.Key).ToList();
            Assert.Empty(leftovers);
        }

        [Fact]
        public void EveryXamlKey_Exists_AndEveryResourceIsUsed()
        {
            var keys = ReadResx("Strings.resx").Keys.ToHashSet();
            var xaml = Directory.GetFiles(SourceDir, "*.xaml", SearchOption.TopDirectoryOnly)
                .Select(f => Regex.Replace(File.ReadAllText(f), "<!--.*?-->", "", RegexOptions.Singleline)).ToList();
            var code = Directory.GetFiles(SourceDir, "*.cs", SearchOption.TopDirectoryOnly).Select(File.ReadAllText).ToList();

            var xamlKeys = xaml.SelectMany(t => Regex.Matches(t, @"\{l:Loc\s+([A-Za-z0-9_]+)\}").Select(m => m.Groups[1].Value)).ToHashSet();
            Assert.Empty(xamlKeys.Except(keys)); // XAML'de olup kaynakta olmayan anahtar (çalışırken [Anahtar] görünürdü)

            var codeKeys = code.SelectMany(t => Regex.Matches(t, @"\bStrings\.([A-Za-z0-9_]+)\b").Select(m => m.Groups[1].Value)).ToHashSet();
            var unused = keys.Except(xamlKeys).Except(codeKeys).ToList();
            Assert.Empty(unused); // kullanılmayan metin kalmasın
        }

        [Fact]
        public void XamlHasNoHardcodedTurkishText()
        {
            var offenders = new List<string>();
            foreach (var file in Directory.GetFiles(SourceDir, "*.xaml"))
            {
                // Yorumları atla; Text/Content/Header/Title/ToolTip özniteliklerinde Türkçe harf olmamalı.
                string text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline);
                foreach (Match m in Regex.Matches(text, "(?:Text|Content|Header|Title|ToolTip)=\"([^\"{][^\"]*)\""))
                    if (m.Groups[1].Value.IndexOfAny("çÇğĞıİöÖşŞüÜ".ToCharArray()) >= 0 && m.Groups[1].Value != "Türkçe")
                        offenders.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
            }
            Assert.Empty(offenders);
        }

        [Theory]
        [InlineData(AppLanguage.Turkish, "en-US", "tr-TR")]
        [InlineData(AppLanguage.English, "tr-TR", "en-US")]
        [InlineData(AppLanguage.System, "tr-TR", "tr-TR")]
        [InlineData(AppLanguage.System, "de-DE", "en-US")] // desteklenmeyen dil → İngilizce
        [InlineData(AppLanguage.System, "en-GB", "en-US")]
        public void UICulture_IsResolved(AppLanguage language, string windows, string expected) =>
            Assert.Equal(expected, Localization.ResolveUICulture(language, CultureInfo.GetCultureInfo(windows)).Name);

        [Theory]
        [InlineData("en-US", "en-GB", "en-GB")] // İngilizce arayüz + İngiltere bölgesi: bölge biçimi korunur
        [InlineData("en-US", "de-DE", "de-DE")]
        [InlineData("en-US", "tr-TR", "en-US")] // Türkçe Windows'ta İngilizce arayüz: İngilizce biçim
        [InlineData("tr-TR", "en-US", "tr-TR")] // İngilizce Windows'ta Türkçe arayüz: Türkçe biçim
        [InlineData("tr-TR", "tr-TR", "tr-TR")]
        public void FormatCulture_FollowsRegionUnlessItConflicts(string ui, string region, string expected) =>
            Assert.Equal(expected, Localization.ResolveFormatCulture(CultureInfo.GetCultureInfo(ui), CultureInfo.GetCultureInfo(region)).Name);

        [Fact]
        public void English_IsUsedEverywhere_WhenSelected()
        {
            Localization.Apply(AppLanguage.English);
            Assert.Equal("ClipStack Options", Strings.Opt_Title);
            Assert.Equal("General", Localization.Get("Tab_General"));
            Assert.Equal("Ctrl+Shift+Space", Settings.FormatHotkey(Native.MOD_CONTROL | Native.MOD_SHIFT, 0x20));

            var text = new ClipItem { Kind = "text", Head = "a\nb", Length = 1234, LineCount = 2, SourceApp = "notepad", LastUsed = DateTime.Now };
            Assert.Contains("2 lines", text.Meta);
            Assert.Contains("characters", text.Meta);
            var image = new ClipItem { Kind = "image", Width = 800, Height = 600, LastUsed = DateTime.Now };
            Assert.Equal("Image  800×600", image.Preview);

            Localization.Apply(AppLanguage.Turkish);
            Assert.Equal("ClipStack Seçenekleri", Strings.Opt_Title);
            Assert.Contains("2 satır", text.Meta);
            Assert.Equal("Görsel  800×600", image.Preview);
        }

        [Fact]
        public void UnknownKey_IsVisibleInsteadOfCrashing() =>
            Assert.Equal("[Yok_Boyle_Bir_Anahtar]", Localization.Get("Yok_Boyle_Bir_Anahtar"));

        [Fact]
        public void LanguageSetting_IsStoredAsText()
        {
            var s = new AppSettings { Language = AppLanguage.English };
            string json = System.Text.Json.JsonSerializer.Serialize(s);
            Assert.Contains("\"Language\":\"English\"", json);
            Assert.Equal(AppLanguage.System, System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.Language);
        }

        [Theory]
        [InlineData(new[] { "--after", "1234" }, 1234)]
        [InlineData(new[] { "--startup", "--AFTER", "42" }, 42)]
        [InlineData(new[] { "--after" }, null)]
        [InlineData(new[] { "--after", "abc" }, null)]
        [InlineData(new string[0], null)]
        public void RestartArgument_IsParsed(string[] args, int? expected) =>
            Assert.Equal(expected, App.ParseAfterArg(args));
    }
}
