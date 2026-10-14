using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClipStack.Tests
{
    /// <summary>Settings.Current'ı değiştiren test sınıfları bu koleksiyonda sırayla çalışır.</summary>
    [CollectionDefinition("Settings")]
    public class SettingsCollection { }

    internal static class TestSetup
    {
        /// <summary>Testler kullanıcının gerçek log.txt dosyasına yazmasın.</summary>
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init() =>
            Log.PathOverride = Path.Combine(Path.GetTempPath(), "ClipStackTests.log");
    }

    [Collection("Settings")]
    public sealed class FeatureTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;
        private readonly ClipDatabase _db;

        public FeatureTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ClipStackTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "history.db");
            Settings.Current.MaxHistory = Settings.MaxHistoryLimit;
            Settings.Current.MaxImages = 100;
            Settings.Current.ExpireEnabled = false;
            _db = new ClipDatabase(_path, TestKeys.Key);
        }

        public void Dispose()
        {
            _db.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // ---------- Dosyalar (CF_HDROP) ----------

        [Fact]
        public void Files_AreStored_SearchedAndShownByName()
        {
            string[] files = { @"C:\Belgeler\Rapor 2026.docx", @"C:\Resimler\tatil.jpg" };
            _db.AddFiles(files, "explorer");

            var item = _db.Search(null).Single();
            Assert.True(item.IsFiles);
            Assert.True(item.ShowsText);
            Assert.Equal("📁 Rapor 2026.docx, tatil.jpg", item.Preview);
            Assert.Contains("2 dosya", item.Meta);
            Assert.Equal(files, _db.GetFiles(item.Id));

            Assert.Single(_db.Search("rapor"));
            Assert.Single(_db.Search("DOSYA"));
            Assert.Single(_db.Search("resimler tatil"));
            Assert.Equal(1, _db.GetStats().Files);
        }

        [Fact]
        public void Files_SameListTwice_IsOneRecord_AndFolderPathsShowName()
        {
            _db.AddFiles(new[] { @"C:\Proje\" }, "explorer");
            _db.AddFiles(new[] { @"C:\Proje\" }, "explorer");
            var item = _db.Search(null).Single();
            Assert.Equal("📁 Proje", item.Preview);
        }

        // ---------- Silme / geri alma ----------

        [Fact]
        public void Delete_ThenRestore_BringsBackSameRecordWithAllData()
        {
            byte[] png = MakePng(30, 20);
            _db.AddImage(png, "mspaint");
            _db.AddOrTouch("biçimli", new byte[] { 1, 2, 3 }, null, "WINWORD");
            var img = _db.Search("görsel").Single();
            var txt = _db.Search("biçimli").Single();
            _db.TogglePin(txt.Id);

            var deletedImg = _db.Delete(img.Id);
            var deletedTxt = _db.Delete(txt.Id);
            Assert.NotNull(deletedImg);
            Assert.Empty(_db.Search(null));

            Assert.True(_db.Restore(deletedTxt!));
            Assert.True(_db.Restore(deletedImg!));

            var all = _db.Search(null);
            Assert.Equal(2, all.Count);
            var back = all.Single(i => i.Id == txt.Id);
            Assert.True(back.Pinned);
            Assert.True(back.HasFormat);
            Assert.Equal("WINWORD", back.SourceApp);
            Assert.Equal(png, _db.GetImage(img.Id));
        }

        [Fact]
        public void Restore_AfterSameContentCopiedAgain_DoesNotDuplicate()
        {
            _db.AddOrTouch("tekrar", null, null, null);
            var deleted = _db.Delete(_db.Search(null).Single().Id)!;
            _db.AddOrTouch("tekrar", null, null, null);

            Assert.False(_db.Restore(deleted));
            Assert.Single(_db.Search(null));
        }

        [Fact]
        public void Delete_Missing_ReturnsNull() => Assert.Null(_db.Delete(12345));

        // ---------- Otomatik sıkıştırma ----------

        [Fact]
        public async System.Threading.Tasks.Task DeletingImages_ReleasesSpace_Automatically()
        {
            for (int i = 0; i < 20; i++)
                _db.AddImage(MakeNoisePng(200, 200, seed: i), null);
            long before = Pragma("page_count");

            foreach (var item in _db.Search(null))
                _db.Delete(item.Id);
            await _db.WaitForMaintenanceAsync(); // temizlik arka planda çalışır

            Assert.Equal(0, Pragma("freelist_count"));
            Assert.True(Pragma("page_count") < before / 2, $"sayfa sayısı {before} → {Pragma("page_count")}");
        }

        [Fact]
        public void OldDatabase_WithoutAutoVacuum_IsMigrated()
        {
            string oldPath = Path.Combine(_dir, "old.db");
            using (var conn = new SqliteConnection($"Data Source={oldPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"CREATE TABLE clips (id INTEGER PRIMARY KEY AUTOINCREMENT, text TEXT NOT NULL,
                                    hash TEXT NOT NULL UNIQUE, source_app TEXT, created_utc INTEGER NOT NULL,
                                    last_used_utc INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0);
                                    INSERT INTO clips (text, hash, created_utc, last_used_utc) VALUES ('eski', 'h1', 1, 1);";
                cmd.ExecuteNonQuery();
            }

            using (var db = new ClipDatabase(oldPath, TestKeys.Key))
                Assert.Equal("eski", db.Search(null).Single().Head);

            Assert.Equal(2, Pragma("auto_vacuum", oldPath)); // INCREMENTAL
        }

        // ---------- Görsel sınırı (ayar) ----------

        [Fact]
        public void MaxImages_Setting_LimitsUnpinnedImages()
        {
            Settings.Current.MaxImages = 3;
            for (int i = 0; i < 5; i++)
                _db.AddImage(MakeNoisePng(4, 4, seed: i), null);
            Assert.Equal(3, _db.GetStats().Images);
        }

        // ---------- Yedekleme ----------

        [Fact]
        public void Backup_ThenImportIntoAnotherDatabase_MergesWithoutDuplicates()
        {
            _db.AddOrTouch("yedekteki metin", null, null, "notepad");
            _db.AddImage(MakePng(5, 5), null);
            _db.AddFiles(new[] { @"C:\a.txt" }, "explorer");
            _db.TogglePin(_db.Search("yedekteki").Single().Id);

            string backup = Path.Combine(_dir, "yedek.db");
            _db.BackupTo(backup);
            Assert.True(File.Exists(backup));

            string otherPath = Path.Combine(_dir, "other.db");
            using var other = new ClipDatabase(otherPath, TestKeys.Key);
            other.AddOrTouch("yedekteki metin", null, null, "notepad"); // zaten var: tekrar eklenmemeli
            other.AddOrTouch("diğer makinedeki", null, null, null);

            Assert.Equal(2, other.ImportFrom(backup));
            Assert.Equal(4, other.Search(null).Count);
            Assert.Single(other.Search("görsel"));
            Assert.Single(other.Search("a.txt"));
            Assert.Equal(0, other.ImportFrom(backup)); // ikinci kez: hiçbir şey eklenmez
        }

        [Fact]
        public void Import_RejectsInvalidFiles()
        {
            string junk = Path.Combine(_dir, "junk.db");
            File.WriteAllText(junk, "bu bir veritabanı değil");
            Assert.Throws<InvalidDataException>(() => _db.ImportFrom(junk));

            string otherSchema = Path.Combine(_dir, "baska.db");
            using (var conn = new SqliteConnection($"Data Source={otherSchema};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE notes (id INTEGER, body TEXT);";
                cmd.ExecuteNonQuery();
            }
            Assert.Throws<InvalidDataException>(() => _db.ImportFrom(otherSchema));
            Assert.Throws<InvalidOperationException>(() => _db.ImportFrom(_path));
            Assert.Throws<InvalidOperationException>(() => _db.BackupTo(_path));
        }

        [Fact]
        public void AutoBackup_RunsOncePerInterval_AndKeepsOnlyNewest()
        {
            string folder = Path.Combine(_dir, "Backups");
            Directory.CreateDirectory(folder);
            for (int i = 1; i <= 8; i++)
            {
                string old = Path.Combine(folder, $"history-2020010{i % 10}-00000{i}.db");
                File.WriteAllText(old, "x");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10 - i));
            }

            string? made = _db.AutoBackupIfDue(folder, TimeSpan.FromDays(1), keep: 3);
            Assert.NotNull(made);
            Assert.Equal(3, Directory.GetFiles(folder, "history-*.db").Length);
            Assert.Contains(made, Directory.GetFiles(folder));

            Assert.Null(_db.AutoBackupIfDue(folder, TimeSpan.FromDays(1), keep: 3)); // bugün zaten alındı
        }

        [Fact]
        public void ExportTexts_WritesOnlyTextRecords()
        {
            _db.AddOrTouch("birinci satır\nikinci satır", null, null, "notepad");
            _db.AddImage(MakePng(5, 5), null);
            _db.AddFiles(new[] { @"C:\gizli.txt" }, null);

            string txt = Path.Combine(_dir, "out.txt");
            Assert.Equal(1, _db.ExportTexts(txt));
            string content = File.ReadAllText(txt);
            Assert.Contains("birinci satır\nikinci satır", content.Replace("\r\n", "\n"));
            Assert.Contains("notepad", content);
            Assert.DoesNotContain("gizli.txt", content);
        }

        // ---------- yardımcılar ----------

        private long Pragma(string name, string? path = null)
        {
            using var conn = ClipDatabase.OpenConnection(path ?? _path, TestKeys.Key);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA {name};";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private static byte[] MakePng(int w, int h) => MakeNoisePng(w, h, seed: 0, noise: false);

        /// <summary>Gürültülü görsel: PNG sıkıştıramaz, böylece veritabanında gerçekten yer kaplar.</summary>
        private static byte[] MakeNoisePng(int w, int h, int seed, bool noise = true)
        {
            var rnd = new Random(seed);
            var pixels = new byte[w * h * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = noise ? (byte)rnd.Next(256) : (byte)200;
                pixels[i + 1] = noise ? (byte)rnd.Next(256) : (byte)seed;
                pixels[i + 2] = noise ? (byte)rnd.Next(256) : (byte)0;
                pixels[i + 3] = 255;
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
    }

    [Collection("Settings")]
    public class SettingsTests
    {
        [Theory]
        [InlineData(Native.MOD_CONTROL | Native.MOD_SHIFT, 0x56u, "Ctrl+Shift+V")]
        [InlineData(Native.MOD_CONTROL | Native.MOD_ALT, 0x31u, "Ctrl+Alt+1")]
        [InlineData(Native.MOD_ALT | Native.MOD_WIN, 0x20u, "Alt+Win+Boşluk")]
        [InlineData(Native.MOD_CONTROL, 0x70u, "Ctrl+F1")]
        public void FormatHotkey_IsReadable(uint mods, uint vk, string expected) =>
            Assert.Equal(expected, Settings.FormatHotkey(mods, vk));

        [Theory]
        [InlineData(Native.MOD_CONTROL, 0x56u, true)]
        [InlineData(Native.MOD_ALT, 0x56u, true)]
        [InlineData(Native.MOD_SHIFT, 0x56u, false)]    // sadece Shift: yazmayı engeller
        [InlineData(Native.MOD_WIN, 0x56u, false)]
        [InlineData(Native.MOD_CONTROL, 0xA0u, false)]  // tuş olarak Sol Shift
        [InlineData(Native.MOD_CONTROL, 0u, false)]
        public void IsValidHotkey_RequiresCtrlOrAltAndARealKey(uint mods, uint vk, bool valid) =>
            Assert.Equal(valid, Settings.IsValidHotkey(mods, vk));

        [Fact]
        public void NormalizeAppList_TrimsExeAndDuplicates() =>
            Assert.Equal(new[] { "chrome", "KeePass" },
                Settings.NormalizeAppList(new[] { " chrome.exe ", "", "Chrome", "KeePass\r", "  " }));

        [Fact]
        public void Clamp_FixesInvalidValues()
        {
            var s = new AppSettings { HotkeyModifiers = Native.MOD_SHIFT, HotkeyKey = 0x56, MaxImages = 0, ExcludedApps = null!,
                                     DefaultsVersion = Settings.CurrentDefaultsVersion };
            Settings.Clamp(s);
            Assert.Equal(Settings.DefaultHotkeyModifiers, s.HotkeyModifiers);
            Assert.Equal(Settings.DefaultHotkeyKey, s.HotkeyKey);
            Assert.Equal(Settings.MinImages, s.MaxImages);
            Assert.Empty(s.ExcludedApps);
        }

        [Fact]
        public void OldSettingsFile_GetsDefaultsForNewFields()
        {
            var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                "{\"MaxHistory\":800,\"ExpireEnabled\":true,\"ExpireDays\":10}")!;
            Settings.Clamp(s);
            Assert.Equal(800, s.MaxHistory);
            Assert.Equal(100, s.MaxImages);
            Assert.Equal("Ctrl+Shift+V", Settings.FormatHotkey(s.HotkeyModifiers, s.HotkeyKey));
            Assert.Contains("KeePass", s.ExcludedApps);
            Assert.False(s.AutoBackup);
        }

        [Fact]
        public void IsExcludedApp_IsCaseInsensitive()
        {
            var saved = Settings.Current.ExcludedApps;
            try
            {
                Settings.Current.ExcludedApps = new() { "KeePass", "chrome" };
                Assert.True(Settings.IsExcludedApp("keepass"));
                Assert.True(Settings.IsExcludedApp("CHROME"));
                Assert.False(Settings.IsExcludedApp("notepad"));
                Assert.False(Settings.IsExcludedApp(null));
            }
            finally
            {
                Settings.Current.ExcludedApps = saved;
            }
        }
    }
}
