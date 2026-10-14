using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;

namespace ClipStack
{
    /// <summary>
    /// Panodan okunan görsel. Ya hazır PNG baytlarıdır (tarayıcı, Office, Ekran Alıntısı Aracı "PNG" formatı
    /// verir) ya da klasik bitmap'tir (PNG'ye arka planda çevrilecek). Bitmap'in sahipliği bu nesneye geçer.
    /// </summary>
    public sealed class ClipboardImage : IDisposable
    {
        public ClipboardImage(byte[] png) => Png = png;
        public ClipboardImage(Bitmap bitmap) => Bitmap = bitmap;

        public byte[]? Png { get; }
        public Bitmap? Bitmap { get; }

        public void Dispose() => Bitmap?.Dispose();
    }

    /// <summary>Veritabanına yazılmaya hazır görsel: PNG, küçük resim, boyutlar ve tekrar engelleme özeti.</summary>
    public sealed record PreparedImage(byte[] Png, byte[] Thumbnail, int Width, int Height, string Hash);

    /// <summary>
    /// Görsel hazırlama (PNG'ye çevirme, küçük resim, özet). Büyük ekran görüntülerinde yüzlerce milisaniye
    /// sürebildiği için uygulama bunu arka plan iş parçacığında çalıştırır. Sadece GDI+ (System.Drawing)
    /// kullanır; WPF görsel sınıfları iş parçacığına bağlıdır, GDI+ ise thread pool'da güvenle çalışır.
    /// </summary>
    public static class ImageProcessing
    {
        /// <summary>Küçük resmin en fazla yüksekliği; çok geniş görsellerde (panorama) en fazla genişliği.</summary>
        public const int ThumbHeight = 96;
        public const int ThumbMaxWidth = 320;

        /// <summary>Görseli hazırlar. PNG <see cref="Settings.MaxImageBytes"/>'tan büyükse veya okunamıyorsa null.</summary>
        public static PreparedImage? Prepare(ClipboardImage image)
        {
            byte[] png;
            if (image.Png != null)
            {
                png = image.Png;
            }
            else
            {
                using var ms = new MemoryStream();
                image.Bitmap!.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }
            return Prepare(png);
        }

        public static PreparedImage? Prepare(byte[] png)
        {
            if (png.Length == 0 || png.Length > Settings.MaxImageBytes)
                return null;

            Bitmap source;
            try
            {
                source = new Bitmap(new MemoryStream(png));
            }
            catch (ArgumentException)
            {
                return null; // geçerli bir görsel değil
            }

            using (source)
            {
                int w = source.Width, h = source.Height;
                var (tw, th) = ThumbnailSize(w, h);
                using var thumb = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    using var attrs = new ImageAttributes();
                    attrs.SetWrapMode(WrapMode.TileFlipXY); // kenarlarda koyu çizgi olmasın
                    g.DrawImage(source, new Rectangle(0, 0, tw, th), 0, 0, w, h, GraphicsUnit.Pixel, attrs);
                }
                using var outMs = new MemoryStream();
                thumb.Save(outMs, ImageFormat.Png);
                string hash = "img:" + Convert.ToHexString(SHA256.HashData(png));
                return new PreparedImage(png, outMs.ToArray(), w, h, hash);
            }
        }

        /// <summary>
        /// En-boy oranını koruyan küçük resim boyutu: normalde yükseklik en fazla 96 px; görsel yüksekliğinin
        /// üç katından genişse (panorama, tablo satırı) genişlik en fazla 320 px. Asla büyütmez.
        /// </summary>
        internal static (int Width, int Height) ThumbnailSize(int width, int height)
        {
            if (width > height * 3)
            {
                int tw = Math.Min(width, ThumbMaxWidth);
                return (tw, Math.Max(1, (int)Math.Round(height * (double)tw / width)));
            }
            int th = Math.Min(height, ThumbHeight);
            return (Math.Max(1, (int)Math.Round(width * (double)th / height)), th);
        }
    }
}
