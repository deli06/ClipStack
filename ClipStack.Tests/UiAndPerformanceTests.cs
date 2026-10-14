using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;
using P = ClipStack.WindowPlacement;

namespace ClipStack.Tests
{
    [Collection("Settings")]
    public sealed class CultureIndependentSearchTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClipStackTests_" + Guid.NewGuid().ToString("N"));
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

        public CultureIndependentSearchTests()
        {
            Directory.CreateDirectory(_dir);
            Settings.Current.MaxHistory = Settings.MaxHistoryLimit;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("tr-TR")]
        [InlineData("")] // InvariantCulture
        public void TurkishCaseFolding_DoesNotDependOnSystemLanguage(string culture)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            using var db = new ClipDatabase(Path.Combine(_dir, culture + ".db"), TestKeys.Key);
            db.AddOrTouch("İSTANBUL IŞIK ŞEKER ÇİĞDEM", null, null, "WINWORD");

            Assert.Single(db.Search("istanbul"));
            Assert.Single(db.Search("ışık"));
            Assert.Single(db.Search("şeker çiğdem"));
            Assert.Single(db.Search("winword"));
            Assert.Empty(db.Search("yok"));
        }
    }

    public class WindowPlacementTests
    {
        // %100'lük birincil monitör (1920×1080, görev çubuğu altta 40 px) ve sağında %150'lik 4K monitör.
        private static readonly P.PxRect Primary = new(0, 0, 1920, 1040);
        private static readonly P.PxRect Right4K = new(1920, 0, 1920 + 3840, 2100);
        // Solda, birincilin üstüne doğru kaydırılmış negatif koordinatlı monitör.
        private static readonly P.PxRect LeftNegative = new(-1280, -300, 0, 724);

        [Theory]
        [InlineData(560, 96, 560)]
        [InlineData(560, 144, 840)]
        [InlineData(460, 120, 575)]
        [InlineData(560, 192, 1120)]
        public void DipToPx_UsesTargetMonitorDpi(double dip, uint dpi, int px) =>
            Assert.Equal(px, P.DipToPx(dip, dpi));

        [Fact]
        public void NearCursor_OpensBottomRightOfCursor_WhenItFits()
        {
            Assert.Equal((508, 308), P.NearCursor(500, 300, Primary, 560, 460, 8));
        }

        [Fact]
        public void NearCursor_FlipsAboveCursor_NearBottomEdge()
        {
            var (x, y) = P.NearCursor(500, 1000, Primary, 560, 460, 8);
            Assert.Equal(508, x);
            Assert.Equal(1000 - 460 - 8, y);
        }

        [Fact]
        public void NearCursor_ShiftsLeft_NearRightEdge()
        {
            var (x, _) = P.NearCursor(1900, 300, Primary, 560, 460, 8);
            Assert.Equal(1920 - 560, x);
        }

        [Fact]
        public void OnHighDpiSecondMonitor_SizeIsScaledAndWindowStaysOnThatMonitor()
        {
            // İmleç 4K monitörün sağ alt köşesinde. 560×460 DIP, %150'de 840×690 px.
            int w = P.DipToPx(560, 144), h = P.DipToPx(460, 144);
            var (x, y) = P.NearCursor(Right4K.Right - 5, Right4K.Bottom - 5, Right4K, w, h, P.DipToPx(8, 144));
            Assert.True(Right4K.Contains(x, y));
            Assert.True(x + w <= Right4K.Right && y + h <= Right4K.Bottom);
            Assert.True(x >= Right4K.Left, "pencere birincil monitöre taşmamalı");
        }

        [Fact]
        public void NegativeCoordinateMonitor_IsHandled()
        {
            var (x, y) = P.NearCursor(-20, -290, LeftNegative, 560, 460, 8);
            Assert.True(LeftNegative.Contains(x, y));
            Assert.True(x + 560 <= LeftNegative.Right);
            Assert.Equal(-290 + 8, y);
        }

        [Fact]
        public void WindowLargerThanWorkArea_SnapsToTopLeft()
        {
            var small = new P.PxRect(100, 100, 500, 400);
            Assert.Equal((100, 100), P.NearCursor(300, 300, small, 800, 600, 8));
            Assert.Equal((100, 100), P.Center(small, 800, 600));
        }

        [Fact]
        public void Center_And_ClampInto()
        {
            Assert.Equal((680, 290), P.Center(Primary, 560, 460));
            // Kaydedilen konum artık ekran dışında (monitör çıkarılmış): içeri çekilir.
            Assert.Equal((1920 - 560, 1040 - 460), P.ClampInto(5000, 5000, Primary, 560, 460));
            Assert.Equal((0, 0), P.ClampInto(-3000, -3000, Primary, 560, 460));
        }
    }

    public class ImageProcessingTests
    {
        [Theory]
        [InlineData(1920, 1080, 171, 96)]
        [InlineData(50, 40, 50, 40)]       // küçük görsel büyütülmez
        [InlineData(4000, 500, 320, 40)]   // panorama: genişlik sınırı
        [InlineData(300, 2000, 14, 96)]    // uzun dikey
        [InlineData(1, 1, 1, 1)]
        public void ThumbnailSize_KeepsAspectRatio(int w, int h, int tw, int th) =>
            Assert.Equal((tw, th), ImageProcessing.ThumbnailSize(w, h));

        [Fact]
        public async Task Prepare_WorksOnBackgroundThread_FromPngAndFromBitmap()
        {
            byte[] png = MakePng(1200, 800);
            int uiThread = Environment.CurrentManagedThreadId;

            var (prepared, thread) = await Task.Run(() => (ImageProcessing.Prepare(png), Environment.CurrentManagedThreadId));
            Assert.NotEqual(uiThread, thread);
            Assert.NotNull(prepared);
            Assert.Equal((1200, 800), (prepared!.Width, prepared.Height));
            Assert.StartsWith("img:", prepared.Hash);
            using (var thumb = new Bitmap(new MemoryStream(prepared.Thumbnail)))
                Assert.Equal((144, 96), (thumb.Width, thumb.Height));

            // Klasik bitmap (CF_DIB) yolu: PNG'ye arka planda çevrilir.
            using var bmp = new Bitmap(new MemoryStream(png));
            using var source = new ClipboardImage(new Bitmap(bmp));
            var fromBitmap = await Task.Run(() => ImageProcessing.Prepare(source));
            Assert.NotNull(fromBitmap);
            Assert.Equal((1200, 800), (fromBitmap!.Width, fromBitmap.Height));
        }

        [Fact]
        public void Prepare_RejectsInvalidAndOversized()
        {
            Assert.Null(ImageProcessing.Prepare(new byte[] { 1, 2, 3 }));
            Assert.Null(ImageProcessing.Prepare(Array.Empty<byte>()));
            Assert.Null(ImageProcessing.Prepare(new byte[Settings.MaxImageBytes + 1]));
        }

        internal static byte[] MakePng(int w, int h, bool noise = false, int seed = 1)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rnd = new Random(seed);
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, h), Color.SteelBlue, Color.Orange, 30f))
                g.FillRectangle(brush, 0, 0, w, h);
            if (noise)
            {
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                var buf = new byte[data.Stride * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                for (int i = 0; i < buf.Length; i += 4) buf[i] ^= (byte)rnd.Next(64);
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
                bmp.UnlockBits(data);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    [Collection("Settings")]
    public sealed class BackgroundDatabaseTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClipStackTests_" + Guid.NewGuid().ToString("N"));
        private readonly ClipDatabase _db;

        public BackgroundDatabaseTests()
        {
            Directory.CreateDirectory(_dir);
            Settings.Current.MaxHistory = Settings.MaxHistoryLimit;
            Settings.Current.MaxImages = 100;
            _db = new ClipDatabase(Path.Combine(_dir, "h.db"), TestKeys.Key);
        }

        public void Dispose()
        {
            _db.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public async Task CompactAsync_RunsOffThread_WhileMainConnectionKeepsWorking()
        {
            for (int i = 0; i < 30; i++)
                _db.AddImage(ImageProcessingTests.MakePng(300, 300, noise: true, seed: i), null);
            foreach (var item in _db.Search(null).Take(25))
                _db.Delete(item.Id);

            var compact = _db.CompactAsync();
            // Sıkıştırma sürerken ana bağlantı okuyup yazabilmeli (arayüz bunu yapacak).
            _db.AddOrTouch("sıkıştırma sırasında", null, null, null);
            Assert.NotEmpty(_db.Search("sıkıştırma"));
            await compact;

            Assert.Equal(6, _db.Search(null).Count);
            Assert.NotNull(_db.GetImage(_db.Search("görsel").First().Id));
        }

        [Fact]
        public async Task BackupImportExport_WorkFromBackgroundThreads()
        {
            _db.AddOrTouch("arka plan metni", null, null, "notepad");
            _db.AddImage(ImageProcessingTests.MakePng(40, 40), null);
            string backup = Path.Combine(_dir, "b.db"), txt = Path.Combine(_dir, "t.txt");

            await Task.Run(() => _db.BackupTo(backup));
            int exported = await Task.Run(() => _db.ExportTexts(txt));
            Assert.Equal(1, exported);

            using var other = new ClipDatabase(Path.Combine(_dir, "o.db"), TestKeys.Key);
            int added = await Task.Run(() => other.ImportFrom(backup));
            other.InvalidateThumbnails();
            Assert.Equal(2, added);
            Assert.NotNull(other.Search("görsel").Single().Thumbnail);
        }
    }

    public class ThemeTests
    {
        [Theory]
        [InlineData(AppTheme.Dark, false, true)]
        [InlineData(AppTheme.Dark, true, true)]
        [InlineData(AppTheme.Light, true, false)]
        [InlineData(AppTheme.System, true, true)]
        [InlineData(AppTheme.System, false, false)]
        public void ResolveDark(AppTheme theme, bool systemDark, bool expected) =>
            Assert.Equal(expected, ThemeManager.ResolveDark(theme, systemDark));

        [Fact]
        public void DarkAndLight_DefineTheSameBrushes_AndControlStylesLoad()
        {
            string[]? dark = null, light = null;
            int styles = 0;
            Exception? error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var d = Load("Dark.xaml");
                    var l = Load("Light.xaml");
                    dark = d.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToArray();
                    light = l.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToArray();
                    styles = Load("Controls.xaml").Count;
                }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();

            Assert.Null(error);
            Assert.Equal(dark, light);
            Assert.Contains("AccentBrush", dark!);
            Assert.True(styles >= 6, "Button, TextBox, CheckBox, RadioButton, TabControl, TabItem");
        }

        private static System.Windows.ResourceDictionary Load(string file) =>
            (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri($"/ClipStack;component/Themes/{file}", UriKind.Relative));

        [Fact]
        public void Settings_StoreEnumsAsText_AndClampPopupSize()
        {
            var s = new AppSettings { Theme = AppTheme.Light, PopupPlacement = PopupPlacement.Remember, PopupWidth = 10, PopupHeight = double.NaN };
            Settings.Clamp(s);
            string json = System.Text.Json.JsonSerializer.Serialize(s);
            Assert.Contains("\"Theme\":\"Light\"", json);
            Assert.Contains("\"PopupPlacement\":\"Remember\"", json);
            Assert.Equal(Settings.MinPopupWidth, s.PopupWidth);
            Assert.Equal(Settings.DefaultPopupHeight, s.PopupHeight);

            var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
            Assert.Equal(AppTheme.Light, back.Theme);
            Assert.Equal(PopupPlacement.Remember, back.PopupPlacement);
        }
    }
}
