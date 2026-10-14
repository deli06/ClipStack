using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClipStack
{
    /// <summary>
    /// Panoya doğrudan Win32 ile, ham bayt olarak erişir.
    ///
    /// Neden: "HTML Format" panoda UTF-8 olarak durur ama WPF/WinForms bazı sürümlerde onu ANSI
    /// sanıp Türkçe karakterleri bozar (ş → ÅŸ). Baytları hiç yorumlamadan okuyup aynen geri
    /// yazınca hem karakterler hem de CF_HTML başlığındaki bayt ofsetleri doğru kalır.
    /// </summary>
    public static class RawClipboard
    {
        /// <summary>Pano sahibi pencere. EmptyClipboard için gerekli; App başlangıçta atar.</summary>
        public static IntPtr Owner { get; set; }

        public static readonly uint CF_HTML = Native.RegisterClipboardFormat("HTML Format");
        public static readonly uint CF_RTF = Native.RegisterClipboardFormat("Rich Text Format");

        /// <summary>Panodaki bir formatı ham bayt olarak okur. Yoksa veya okunamazsa null.</summary>
        public static byte[]? Read(uint format)
        {
            if (!Native.IsClipboardFormatAvailable(format))
                return null;
            // Okuma pano okuyucu iş parçacığından yapılır; arayüz iş parçacığının penceresi sahip verilmez.
            if (!Open(IntPtr.Zero))
                return null;
            try
            {
                IntPtr h = Native.GetClipboardData(format);
                if (h == IntPtr.Zero) return null;

                IntPtr p = Native.GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try
                {
                    int size = (int)Math.Min((ulong)Native.GlobalSize(h), int.MaxValue);
                    var data = new byte[size];
                    Marshal.Copy(p, data, 0, size);
                    return TrimTrailingZeros(data);
                }
                finally
                {
                    Native.GlobalUnlock(h);
                }
            }
            finally
            {
                Native.CloseClipboard();
            }
        }

        /// <summary>
        /// Panoyu temizleyip metni ve (varsa) HTML/RTF biçimlerini birlikte yazar.
        /// html ve rtf null verilirse sadece düz metin yazılır.
        /// </summary>
        public static bool WriteText(string text, byte[]? html, byte[]? rtf)
        {
            // EmptyClipboard için sahip pencere gerekir.
            if (!Open(Owner))
                return false;
            try
            {
                if (!Native.EmptyClipboard())
                    return false;

                bool ok = SetBytes(Native.CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
                if (html != null && html.Length > 0) SetBytes(CF_HTML, WithTerminator(html));
                if (rtf != null && rtf.Length > 0) SetBytes(CF_RTF, WithTerminator(rtf));
                return ok;
            }
            finally
            {
                Native.CloseClipboard();
            }
        }

        private static bool SetBytes(uint format, byte[] data)
        {
            IntPtr h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (h == IntPtr.Zero) return false;

            IntPtr p = Native.GlobalLock(h);
            if (p == IntPtr.Zero)
            {
                Native.GlobalFree(h);
                return false;
            }
            Marshal.Copy(data, 0, p, data.Length);
            Native.GlobalUnlock(h);

            // Başarılı olursa belleğin sahibi artık sistemdir; biz serbest bırakmayız.
            if (Native.SetClipboardData(format, h) == IntPtr.Zero)
            {
                Native.GlobalFree(h);
                return false;
            }
            return true;
        }

        /// <summary>Pano başka bir uygulamada açıksa kısa aralıklarla yeniden dener.</summary>
        private static bool Open(IntPtr owner)
        {
            for (int i = 0; i < 10; i++)
            {
                if (Native.OpenClipboard(owner))
                    return true;
                Thread.Sleep(30);
            }
            return false;
        }

        private static byte[] TrimTrailingZeros(byte[] data)
        {
            int len = data.Length;
            while (len > 0 && data[len - 1] == 0) len--;
            if (len == data.Length) return data;
            var result = new byte[len];
            Array.Copy(data, result, len);
            return result;
        }

        private static byte[] WithTerminator(byte[] data)
        {
            var result = new byte[data.Length + 1];
            Array.Copy(data, result, data.Length);
            return result;
        }
    }
}
