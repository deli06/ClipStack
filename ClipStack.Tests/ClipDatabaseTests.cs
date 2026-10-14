using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ClipStack.Tests
{
    [Collection("Settings")]
    public sealed class ClipDatabaseTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;
        private readonly string _path;
        private readonly ClipDatabase _db;

        public ClipDatabaseTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "ClipStackTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "history.db");
            Settings.Current.MaxHistory = Settings.MaxHistoryLimit;
            Settings.Current.ExpireEnabled = false;
            _db = new ClipDatabase(_path, TestKeys.Key);
        }

        public void Dispose()
        {
            _db.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void Search_IsTurkishCaseInsensitive()
        {
            _db.AddOrTouch("İSTANBUL'da IŞIK", null, null, "notepad");
            _db.AddOrTouch("başka bir şey", null, null, "notepad");

            Assert.Single(_db.Search("istanbul"));
            Assert.Single(_db.Search("ışık"));
            Assert.Single(_db.Search("ŞEY"));
            Assert.Empty(_db.Search("yok"));
        }

        [Fact]
        public void Search_MatchesEnglishWordsRegardlessOfDottedI()
        {
            _db.AddOrTouch("PRINT the INVOICE", null, null, "WINWORD");
            Assert.Single(_db.Search("print invoice"));
            Assert.Single(_db.Search("winword"));
            Assert.Single(_db.Search("İNVOİCE")); // Türkçe klavyeyle büyük yazılmış
        }

        [Fact]
        public void Search_AllTermsMustMatch_TextOrSourceApp()
        {
            _db.AddOrTouch("merhaba dünya", null, null, "chrome");
            _db.AddOrTouch("merhaba evren", null, null, "WINWORD");

            Assert.Equal(2, _db.Search("merhaba").Count);
            Assert.Single(_db.Search("merhaba   dünya"));
            Assert.Equal("merhaba evren", _db.Search("winword merhaba").Single().Head);
            Assert.Empty(_db.Search("dünya evren"));
        }

        [Fact]
        public void Search_EmptyQuery_ReturnsPinnedFirstThenNewest_RespectingLimit()
        {
            for (int i = 0; i < 10; i++)
                _db.AddOrTouch("kayıt " + i, null, null, null);
            long oldest = _db.Search(null).Last().Id;
            _db.TogglePin(oldest);

            var list = _db.Search("", limit: 4);
            Assert.Equal(4, list.Count);
            Assert.Equal(oldest, list[0].Id);
            Assert.True(list[0].Pinned);
            Assert.Equal("kayıt 9", list[1].Head);
            Assert.Equal("kayıt 8", list[2].Head);
        }

        [Fact]
        public void Search_LoadsOnlyHead_GetTextReturnsFullText()
        {
            string longText = string.Join("\n", Enumerable.Range(0, 1000).Select(i => "satır " + i));
            _db.AddOrTouch(longText, null, null, null);

            var item = _db.Search(null).Single();
            Assert.Equal(300, item.Head.Length);
            Assert.Equal(longText.Length, item.Length);
            Assert.Equal(1000, item.LineCount);
            Assert.EndsWith("…", item.Preview);
            Assert.Contains("1.000 satır", item.Meta); // sayılar dile göre biçimlenir (tr: binlik ayırıcı nokta)
            Assert.Equal(longText, _db.GetText(item.Id));

            // Aranan kelime metnin baş kısmında olmasa da bulunmalı.
            Assert.Single(_db.Search("satır 999"));
        }

        [Fact]
        public void Search_SingleLineText_HasNoLineCountInMeta()
        {
            _db.AddOrTouch("tek satır", null, null, null);
            var item = _db.Search(null).Single();
            Assert.Equal(1, item.LineCount);
            Assert.DoesNotContain("satır ·", item.Meta.Replace("tek satır", ""));
            Assert.Equal("tek satır", item.Preview);
        }

        [Fact]
        public void Images_AreFoundByKeywordAndSize_AndThumbnailLoadsLazily()
        {
            _db.AddImage(MakePng(80, 60), "SnippingTool");
            _db.AddOrTouch("metin", null, null, null);

            var img = _db.Search("görsel").Single();
            Assert.True(img.IsImage);
            Assert.Single(_db.Search("80x60"));
            Assert.Single(_db.Search("RESİM"));
            Assert.Single(_db.Search("snippingtool"));

            Assert.NotNull(img.Thumbnail);
            Assert.Same(img.Thumbnail, _db.GetThumbnail(img.Id)); // önbellekten
            Assert.Null(_db.Search("metin").Single().Thumbnail);
        }

        [Fact]
        public void Delete_RemovesItem_AndClearsThumbnail()
        {
            _db.AddImage(MakePng(10, 10), null);
            var img = _db.Search(null).Single();
            Assert.NotNull(img.Thumbnail);

            _db.Delete(img.Id);
            Assert.Empty(_db.Search(null));
            Assert.Null(_db.GetThumbnail(img.Id));
            Assert.Null(_db.GetImage(img.Id));
        }

        [Fact]
        public void Search_LargeHistory_IsFastAndBounded()
        {
            // 50.000 kayıt doğrudan SQL ile (AddOrTouch her seferinde sınır uyguladığı için yavaş olurdu).
            using (var conn = ClipDatabase.OpenConnection(_path, TestKeys.Key))
            {
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO clips (text, hash, source_app, created_utc, last_used_utc)
                                    VALUES ($t, $h, 'app', $n, $n);";
                var pt = cmd.Parameters.Add("$t", SqliteType.Text);
                var ph = cmd.Parameters.Add("$h", SqliteType.Text);
                var pn = cmd.Parameters.Add("$n", SqliteType.Integer);
                string filler = new string('x', 500);
                for (int i = 0; i < 50_000; i++)
                {
                    pt.Value = $"kayıt {i} {filler}";
                    ph.Value = "h" + i;
                    pn.Value = i;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }

            var sw = Stopwatch.StartNew();
            var all = _db.Search(null);
            long emptyMs = sw.ElapsedMilliseconds;
            Assert.Equal(Settings.MaxListItems, all.Count);
            Assert.Equal("kayıt 49999", all[0].Head.Substring(0, 11));

            sw.Restart();
            var hit = _db.Search("KAYIT 12345 ");
            long hitMs = sw.ElapsedMilliseconds;
            Assert.Contains(hit, i => i.Head.StartsWith("kayıt 12345 "));

            sw.Restart();
            var miss = _db.Search("bulunmayan");
            long missMs = sw.ElapsedMilliseconds;
            Assert.Empty(miss);

            _out.WriteLine($"50.000 kayıt: boş arama {emptyMs} ms, eşleşen {hitMs} ms, eşleşmeyen (tam tarama) {missMs} ms");
            Assert.True(emptyMs < 500, $"boş arama {emptyMs} ms");
            Assert.True(missMs < 3000, $"tam tarama {missMs} ms");
        }

        [Fact]
        public void ApplyLimits_StillKeepsPinnedAndTrimsUnpinned()
        {
            Settings.Current.MaxHistory = Settings.MinHistory; // 10
            try
            {
                _db.AddOrTouch("sabit", null, null, null);
                _db.TogglePin(_db.Search("sabit").Single().Id);
                for (int i = 0; i < 15; i++)
                    _db.AddOrTouch($"öğe {i:00}", null, null, null);

                var all = _db.Search(null);
                Assert.Equal(11, all.Count); // 10 sabitlenmemiş + 1 sabitlenmiş
                Assert.Equal("sabit", all[0].Head);
                Assert.Empty(_db.Search("öğe 04"));
                Assert.Single(_db.Search("öğe 05"));
            }
            finally
            {
                Settings.Current.MaxHistory = Settings.MaxHistoryLimit;
            }
        }

        private static byte[] MakePng(int w, int h)
        {
            var pixels = new byte[w * h * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 3] = 255; }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
    }
}
