using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClipStack.Tests
{
    internal static class TestKeys
    {
        public static readonly byte[] Key = Enumerable.Range(1, DatabaseKey.KeyBytes).Select(i => (byte)i).ToArray();
        public static readonly byte[] Other = Enumerable.Range(100, DatabaseKey.KeyBytes).Select(i => (byte)i).ToArray();
    }

    [Collection("Settings")]
    public sealed class EncryptionTests : IDisposable
    {
        private readonly string _dir;

        public EncryptionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ClipStackTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Settings.Current.MaxHistory = Settings.MaxHistoryLimit;
            Settings.Current.MaxImages = 100;
            Settings.Current.ExpireEnabled = false;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string P(string name) => Path.Combine(_dir, name);

        [Fact]
        public void NewDatabase_IsEncrypted_AndContentNotVisibleOnDisk()
        {
            string path = P("h.db");
            using (var db = new ClipDatabase(path, TestKeys.Key))
            {
                db.AddOrTouch("çok gizli parola Hunter2", null, null, "notepad");
                db.Compact(); // WAL'ı ana dosyaya yaz
            }

            Assert.False(ClipDatabase.IsPlaintextDatabase(path));
            string raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("Hunter2", raw);
            Assert.DoesNotContain("CREATE TABLE", raw);

            using var reopened = new ClipDatabase(path, TestKeys.Key);
            Assert.Equal("çok gizli parola Hunter2", reopened.Search(null).Single().Head);
        }

        [Fact]
        public void WrongKey_OrNoKey_CannotOpen()
        {
            string path = P("h.db");
            using (var db = new ClipDatabase(path, TestKeys.Key))
                db.AddOrTouch("x", null, null, null);

            Assert.Throws<DatabaseKeyException>(() => new ClipDatabase(path, TestKeys.Other));
            Assert.Throws<DatabaseKeyException>(() => ClipDatabase.OpenConnection(path, key: null).Dispose());
        }

        [Fact]
        public void PlaintextDatabase_IsEncryptedInPlace_KeepingDataIdsAndPins()
        {
            string path = P("eski.db");
            using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"PRAGMA journal_mode=WAL;
                    CREATE TABLE clips (id INTEGER PRIMARY KEY AUTOINCREMENT, text TEXT NOT NULL, hash TEXT NOT NULL UNIQUE,
                        source_app TEXT, created_utc INTEGER NOT NULL, last_used_utc INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO clips (id, text, hash, created_utc, last_used_utc, pinned) VALUES (40, 'eski kayıt', 'h40', 1, 1, 1);";
                cmd.ExecuteNonQuery();
            }
            Assert.True(ClipDatabase.IsPlaintextDatabase(path));

            using (var db = new ClipDatabase(path, TestKeys.Key))
            {
                var item = db.Search(null).Single();
                Assert.Equal(40, item.Id);
                Assert.True(item.Pinned);
                db.AddOrTouch("yeni", null, null, null);
                Assert.True(db.Search("yeni").Single().Id > 40); // AUTOINCREMENT sırası korunmuş
            }

            Assert.False(ClipDatabase.IsPlaintextDatabase(path));
            Assert.False(File.Exists(path + ".encrypting"));
            using var check = ClipDatabase.OpenConnection(path, TestKeys.Key);
            using var pragma = check.CreateCommand();
            pragma.CommandText = "PRAGMA auto_vacuum;";
            Assert.Equal(2L, pragma.ExecuteScalar());
        }

        [Fact]
        public void EncryptedBackup_OnlyOpensWithSameKey_PlainBackupOpensAnywhere()
        {
            using var db = new ClipDatabase(P("h.db"), TestKeys.Key);
            db.AddOrTouch("taşınacak", null, null, null);
            db.BackupTo(P("sifreli.db"), encrypted: true);
            db.BackupTo(P("sifresiz.db"), encrypted: false);

            Assert.False(ClipDatabase.IsPlaintextDatabase(P("sifreli.db")));
            Assert.True(ClipDatabase.IsPlaintextDatabase(P("sifresiz.db")));

            // Başka hesap/bilgisayar (başka anahtar): şifresiz yedek açılır, şifreli açılmaz.
            using var elsewhere = new ClipDatabase(P("baska.db"), TestKeys.Other);
            var ex = Assert.Throws<InvalidDataException>(() => elsewhere.ImportFrom(P("sifreli.db")));
            Assert.Contains("şifresiz", ex.Message);
            Assert.Equal(1, elsewhere.ImportFrom(P("sifresiz.db")));

            // Aynı anahtar: şifreli yedek de açılır.
            using var same = new ClipDatabase(P("ayni.db"), TestKeys.Key);
            Assert.Equal(1, same.ImportFrom(P("sifreli.db")));
        }

        [Fact]
        public void PlaintextAutoBackups_AreEncrypted()
        {
            string folder = P("Backups");
            Directory.CreateDirectory(folder);
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(folder, "history-20250101-000000.db")};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (x); INSERT INTO t VALUES ('gizli');";
                cmd.ExecuteNonQuery();
            }
            using var db = new ClipDatabase(P("h.db"), TestKeys.Key);
            Assert.Equal(1, db.EncryptPlaintextBackups(folder));
            Assert.False(ClipDatabase.IsPlaintextDatabase(Path.Combine(folder, "history-20250101-000000.db")));
            Assert.Equal(0, db.EncryptPlaintextBackups(folder));
        }

        [Fact]
        public void DatabaseKey_IsCreatedOnce_ProtectedOnDisk_AndReloaded()
        {
            string keyPath = P("db.key");
            byte[] k1 = DatabaseKey.LoadOrCreate(keyPath);
            byte[] k2 = DatabaseKey.LoadOrCreate(keyPath);
            Assert.Equal(DatabaseKey.KeyBytes, k1.Length);
            Assert.Equal(k1, k2);

            byte[] onDisk = File.ReadAllBytes(keyPath);
            Assert.NotEqual(k1, onDisk);
            Assert.False(onDisk.AsSpan().IndexOf(k1) >= 0); // ham anahtar dosyada düz durmuyor

            File.WriteAllBytes(keyPath, new byte[] { 1, 2, 3 });
            Assert.ThrowsAny<CryptographicException>(() => DatabaseKey.LoadOrCreate(keyPath));
        }
    }

    public class SensitiveContentTests
    {
        // Örnek anahtarlar sahtedir. Kaynakta parçalı yazılırlar ("gh" + "p_…"): derleyici aynı metni üretir,
        // ama dosyada anahtar biçimi görünmez; böylece GitHub'ın gizli anahtar taraması gibi araçlar onları
        // gerçek sanıp push'u engellemez.
        private const string FakeAwsKey = "AK" + "IAIOSFODNN7EXAMPLE";

        [Theory]
        [InlineData("gh" + "p_abcdefghijklmnopqrstuvwxyz0123456789", "GitHub token")]
        [InlineData("export GITHUB=github" + "_pat_11ABCDEFG0123456789_abcdefghijklmnop", "GitHub token")]
        [InlineData("AK" + "IAIOSFODNN7EXAMPLE", "AWS erişim anahtarı")]
        [InlineData("-----BEGIN OPENSSH PRIVATE" + " KEY-----\nb3BlbnNzaC1rZXk=\n-----END OPENSSH PRIVATE" + " KEY-----", "özel anahtar (PEM)")]
        [InlineData("-----BEGIN PRIVATE" + " KEY-----", "özel anahtar (PEM)")]
        [InlineData("xo" + "xb-1234567890-abcdefghij", "Slack token")]
        [InlineData("sk_" + "live_" + "abcdefghijklmnopqrstuvwx", "Stripe gizli anahtarı")]
        [InlineData("ANTHROPIC_API_KEY=sk-" + "ant-api03-abcdefghijklmnopqrstuvwxyz012345", "API anahtarı (sk-…)")]
        [InlineData("AI" + "zaSyA-abcdefghijklmnopqrstuvwxyz01234", "Google API anahtarı")]
        [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9" + ".eyJzdWIiOiIxMjM0NTY3ODkwIn0" + ".dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "JWT")]
        [InlineData("DefaultEndpointsProtocol=https;AccountName=x;Account" + "Key=abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOP0123==", "Azure bağlantı anahtarı")]
        [InlineData("gl" + "pat-abcdefghijklmnopqrst", "GitLab token")]
        [InlineData("np" + "m_abcdefghijklmnopqrstuvwxyz0123456789", "npm token")]
        public void KnownSecrets_AreDetected(string text, string expected) =>
            Assert.Equal(expected, SensitiveContent.FindSecret(text));

        [Theory]
        [InlineData("Merhaba dünya, toplantı saat 15:00'te.")]
        [InlineData("https://github.com/anthropics/claude-code")]
        [InlineData("ghp_kisa")]
        [InlineData("risk-assessment-for-sk-project")]
        [InlineData("SELECT * FROM users WHERE id = 42;")]
        [InlineData("AKIA lowercase akiaiosfodnn7example")]
        public void OrdinaryText_IsNotASecret(string text) =>
            Assert.Null(SensitiveContent.FindSecret(text));

        [Theory]
        [InlineData("Tr0ub4dor&3", true)]
        [InlineData("P@ssw0rd!", true)]
        [InlineData("correcthorsebatterystaple", false)] // tek sınıf
        [InlineData("Merhaba Dünya 2026!", false)]       // boşluk var
        [InlineData("user@example.com", false)]
        [InlineData(@"C:\Windows\System32", false)]
        [InlineData("https://a.b/C1", false)]
        [InlineData("Ab1!", false)]                      // çok kısa
        [InlineData("12345678", false)]
        public void PasswordHeuristic(string text, bool looksLikePassword) =>
            Assert.Equal(looksLikePassword, SensitiveContent.LooksLikePassword(text));

        [Fact]
        public void ShouldSkip_FollowsSettings()
        {
            var s = new AppSettings(); // varsayılan: token kontrolü açık, parola sezgisi kapalı
            Assert.True(SensitiveContent.ShouldSkip(FakeAwsKey, s, out var reason));
            Assert.Equal("AWS erişim anahtarı", reason);
            Assert.False(SensitiveContent.ShouldSkip("Tr0ub4dor&3", s, out _));

            s.SkipPasswordLike = true;
            Assert.True(SensitiveContent.ShouldSkip("Tr0ub4dor&3", s, out reason));
            Assert.Equal("parolaya benzeyen metin", reason);

            s.SkipSecrets = false;
            Assert.False(SensitiveContent.ShouldSkip(FakeAwsKey, s, out _));
        }
    }

    [Collection("Settings")]
    public class ExcludedAppDefaultsTests
    {
        [Fact]
        public void OldSettings_GetNewPasswordManagers_ButRemovedOldDefaultsStayRemoved()
        {
            // v1 kullanıcısı Bitwarden'ı listeden çıkarmış, kendi uygulamasını eklemiş.
            var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
                "{\"ExcludedApps\":[\"KeePass\",\"KeePassXC\",\"1Password\",\"BenimUygulamam\"]}")!;
            Assert.Equal(0, s.DefaultsVersion);
            Settings.Clamp(s);

            Assert.Equal(Settings.CurrentDefaultsVersion, s.DefaultsVersion);
            Assert.Contains("Dashlane", s.ExcludedApps);
            Assert.Contains("LastPass", s.ExcludedApps);
            Assert.Contains("BenimUygulamam", s.ExcludedApps);
            Assert.DoesNotContain("Bitwarden", s.ExcludedApps);

            // İkinci yükleme: tekrar değiştirmez.
            s.ExcludedApps.Remove("Dashlane");
            Settings.Clamp(s);
            Assert.DoesNotContain("Dashlane", s.ExcludedApps);
        }

        [Fact]
        public void NewSettings_HaveFullListAndSafeDefaults()
        {
            var s = new AppSettings();
            Settings.Clamp(s);
            Assert.Contains("Bitwarden", s.ExcludedApps);
            Assert.Contains("NordPass", s.ExcludedApps);
            Assert.True(s.SkipSecrets);
            Assert.False(s.SkipPasswordLike);
        }
    }
}
