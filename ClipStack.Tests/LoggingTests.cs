using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ClipStack.Tests
{
    [Collection("Settings")]
    public sealed class SettingsLoadTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClipStackTests_" + Guid.NewGuid().ToString("N"));
        private readonly string _path;

        public SettingsLoadTests()
        {
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "settings.json");
        }

        public void Dispose()
        {
            Settings.LoadFrom(Path.Combine(_dir, "yok.json")); // diğer testler için varsayılanlara dön
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void CorruptFile_IsBackedUp_Logged_AndReported_ThenDefaultsAreUsed()
        {
            const string corrupt = "{ \"MaxHistory\": 900, \"ExcludedApps\": [\"Benim\" ";   // yarım kalmış JSON
            File.WriteAllText(_path, corrupt);
            long logBefore = LogLength();

            Settings.LoadFrom(_path);

            // Varsayılanlarla devam
            Assert.Equal(500, Settings.Current.MaxHistory);
            // Bozuk dosya silinmeden yedeklendi, içeriği birebir aynı
            string backup = Directory.GetFiles(_dir, "settings.json.bozuk-*").Single();
            Assert.Equal(corrupt, File.ReadAllText(backup));
            // Kullanıcıya gösterilecek açıklama
            Assert.NotNull(Settings.LoadProblem);
            Assert.Contains(Path.GetFileName(backup), Settings.LoadProblem);
            // Log'a yazıldı (hata ayrıntısıyla birlikte)
            string newLog = ReadLogFrom(logBefore);
            Assert.Contains("Ayar dosyası okunamadı", newLog);
            Assert.Contains(backup, newLog);
            Assert.Contains("JsonException", newLog);
        }

        [Fact]
        public void ValidFile_LoadsWithoutProblem()
        {
            File.WriteAllText(_path, "{ \"MaxHistory\": 900 }");
            Settings.LoadFrom(_path);
            Assert.Equal(900, Settings.Current.MaxHistory);
            Assert.Null(Settings.LoadProblem);
            Assert.Empty(Directory.GetFiles(_dir, "*.bozuk-*"));
        }

        [Fact]
        public void MissingFile_UsesDefaults_WithoutProblem()
        {
            Settings.LoadFrom(_path);
            Assert.Equal(500, Settings.Current.MaxHistory);
            Assert.Null(Settings.LoadProblem);
        }

        [Fact]
        public void ProblemFromPreviousLoad_IsClearedOnNextLoad()
        {
            File.WriteAllText(_path, "bozuk");
            Settings.LoadFrom(_path);
            Assert.NotNull(Settings.LoadProblem);

            File.WriteAllText(_path, "{}");
            Settings.LoadFrom(_path);
            Assert.Null(Settings.LoadProblem);
        }

        private static long LogLength() => File.Exists(Log.FilePath) ? new FileInfo(Log.FilePath).Length : 0;

        private static string ReadLogFrom(long offset)
        {
            using var fs = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = Math.Min(offset, fs.Length);
            return new StreamReader(fs).ReadToEnd();
        }
    }

    public class LogTests
    {
        [Fact]
        public void TestsNeverWriteToTheRealLogFile()
        {
            string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipStack", "log.txt");
            Assert.NotEqual(Path.GetFullPath(real), Path.GetFullPath(Log.FilePath));
        }

        [Fact]
        public void ErrorEntries_IncludeLevel_Message_AndExceptionDetails()
        {
            long before = File.Exists(Log.FilePath) ? new FileInfo(Log.FilePath).Length : 0;
            string marker = Guid.NewGuid().ToString("N");
            try { throw new InvalidOperationException("iç ayrıntı " + marker); }
            catch (Exception ex) { Log.Error("test hatası " + marker, ex); }

            using var fs = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = before;
            string written = new StreamReader(fs).ReadToEnd();
            Assert.Contains("[HATA] test hatası " + marker, written);
            Assert.Contains("InvalidOperationException: iç ayrıntı " + marker, written);
            Assert.Contains(nameof(ErrorEntries_IncludeLevel_Message_AndExceptionDetails), written); // yığın izi
        }
    }
}
