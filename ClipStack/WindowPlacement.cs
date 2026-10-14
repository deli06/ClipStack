using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipStack
{
    /// <summary>
    /// Pencereyi fiziksel piksellerle, hedef monitörün kendi DPI'ını kullanarak konumlandırır.
    ///
    /// Neden: WPF'in Left/Top değerleri DIP'tir ve pencerenin o anki monitörünün DPI'ına göre dönüştürülür.
    /// Farklı ölçekli monitörler varsa (ör. %100 dizüstü + %150 harici) pencere imlecin olduğu monitöre
    /// taşınırken yanlış DPI ile hesaplanıp kayar veya ekran dışına taşar. Burada her şey o monitörün
    /// piksel ve DPI değerleriyle hesaplanır, sonra Win32 SetWindowPos ile uygulanır.
    /// </summary>
    public static class WindowPlacement
    {
        public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
        {
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
        }

        public readonly record struct MonitorArea(PxRect WorkArea, uint Dpi);

        // ---------- Saf hesaplar (test edilebilir) ----------

        public static int DipToPx(double dip, uint dpi) => (int)Math.Round(dip * dpi / 96.0);

        /// <summary>
        /// İmlecin sağ altına yerleştirir; sağa sığmazsa sola yaslar, alta sığmazsa imlecin üstüne çıkar.
        /// Sonuç her zaman çalışma alanının içindedir (pencere alandan büyükse sol üst köşeye yaslanır).
        /// </summary>
        public static (int X, int Y) NearCursor(int cursorX, int cursorY, PxRect work, int width, int height, int gap)
        {
            int x = cursorX + gap;
            int y = cursorY + gap;
            if (x + width > work.Right) x = work.Right - width;
            if (y + height > work.Bottom) y = cursorY - height - gap;
            return ClampInto(x, y, work, width, height);
        }

        public static (int X, int Y) Center(PxRect work, int width, int height) =>
            ClampInto(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, work, width, height);

        /// <summary>Pencereyi çalışma alanının içine çeker.</summary>
        public static (int X, int Y) ClampInto(int x, int y, PxRect work, int width, int height)
        {
            x = Math.Min(x, work.Right - width);
            y = Math.Min(y, work.Bottom - height);
            return (Math.Max(work.Left, x), Math.Max(work.Top, y));
        }

        // ---------- Win32 ----------

        /// <summary>Noktanın bulunduğu (yoksa en yakın) monitörün çalışma alanı ve DPI'ı.</summary>
        public static MonitorArea MonitorFromPoint(int x, int y) =>
            Describe(NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST));

        public static MonitorArea MonitorFromWindow(IntPtr hwnd) =>
            Describe(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));

        private static MonitorArea Describe(IntPtr monitor)
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            NativeMethods.GetMonitorInfo(monitor, ref info);
            uint dpi = 96;
            try
            {
                if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
                    dpi = dx;
            }
            catch (DllNotFoundException) { /* Windows 8.1 öncesi: 96 */ }
            var w = info.rcWork;
            return new MonitorArea(new PxRect(w.Left, w.Top, w.Right, w.Bottom), dpi);
        }

        public static PxRect GetWindowRect(Window window)
        {
            NativeMethods.GetWindowRect(Handle(window), out var r);
            return new PxRect(r.Left, r.Top, r.Right, r.Bottom);
        }

        /// <summary>Pencerenin sol üst köşesini fiziksel piksel olarak taşır (boyuta ve sıraya dokunmaz).</summary>
        public static void MoveTo(Window window, int x, int y) =>
            NativeMethods.SetWindowPos(Handle(window), IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        /// <summary>
        /// Pencere taşındıktan sonra (DPI değişip WPF yeniden boyutlandırmış olabilir) gerçek boyutuyla
        /// bulunduğu monitörün çalışma alanına sığdırır.
        /// </summary>
        public static void KeepOnScreen(Window window)
        {
            var rect = GetWindowRect(window);
            var area = MonitorFromWindow(Handle(window));
            var (x, y) = ClampInto(rect.Left, rect.Top, area.WorkArea, rect.Width, rect.Height);
            if (x != rect.Left || y != rect.Top)
                MoveTo(window, x, y);
        }

        private static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();

        private static class NativeMethods
        {
            public const uint MONITOR_DEFAULTTONEAREST = 2;
            public const int MDT_EFFECTIVE_DPI = 0;
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X, Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left, Top, Right, Bottom; }

            [StructLayout(LayoutKind.Sequential)]
            public struct MONITORINFO
            {
                public int cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
            }

            [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
            [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
            [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
            [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
            [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
            [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        }
    }
}
