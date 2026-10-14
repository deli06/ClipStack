using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ClipStack
{
    /// <summary>Metni panoya yazar ve hedef pencereye Ctrl+V gönderir.</summary>
    public static class PasteService
    {
        /// <summary>
        /// Panoya en son bizim yazdığımız andaki sıra numarası. İzleyici bunu görünce
        /// değişikliği yeni bir kopyalama saymaz (istatistik ve geçmiş şişmesin diye).
        /// </summary>
        public static uint LastOwnSequence { get; private set; }

        /// <summary>
        /// Metni panoya yazar. HTML/RTF verilirse biçimli olarak da yazılır; hedef uygulama
        /// (Word, Outlook, tarayıcı) desteklediği en zengin biçimi seçer. "Düz metin olarak
        /// yapıştır" için html ve rtf null verilir.
        /// </summary>
        public static bool SetClipboardText(string text, byte[]? html = null, byte[]? rtf = null)
        {
            for (int i = 0; i < 3; i++)
            {
                if (RawClipboard.WriteText(text, html, rtf))
                {
                    LastOwnSequence = Native.GetClipboardSequenceNumber();
                    return true;
                }
                Thread.Sleep(50);
            }
            return false;
        }

        /// <summary>
        /// Görseli hem "PNG" (şeffaflık korunur; Chrome, Office, Paint.NET vb. okur)
        /// hem de klasik Bitmap formatında (eski uygulamalar ve Paint için) panoya yazar.
        /// İzleyici panodan yine "PNG"yi okuyacağı için aynı baytlar gelir ve kopya kayıt oluşmaz.
        /// </summary>
        /// <exception cref="InvalidDataException">Saklanan görsel bozuksa.</exception>
        public static bool SetClipboardImage(byte[] png)
        {
            System.Drawing.Bitmap bitmap;
            try
            {
                using var src = new System.Drawing.Bitmap(new MemoryStream(png));
                bitmap = new System.Drawing.Bitmap(src); // akıştan bağımsız kopya
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Kayıtlı görsel bozuk, açılamadı.", ex);
            }

            var data = new System.Windows.Forms.DataObject();
            data.SetData(System.Windows.Forms.DataFormats.Bitmap, bitmap);
            data.SetData("PNG", false, new MemoryStream(png));

            try
            {
                // copy=true: uygulama kapansa da pano içeriği kalsın. 10 deneme, 50 ms arayla.
                System.Windows.Forms.Clipboard.SetDataObject(data, true, 10, 50);
                LastOwnSequence = Native.GetClipboardSequenceNumber();
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        /// <summary>
        /// Dosya listesini panoya yazar: Gezgin için CF_HDROP (her zaman "kopyala", asla "taşı";
        /// dosya kesilmiş olsa bile geçmişten yapıştırma kaynağı silmemeli), metin editörleri için
        /// de satır başına bir yol. Yolların artık var olup olmadığı kontrol edilmez.
        /// </summary>
        public static bool SetClipboardFiles(string[] paths)
        {
            var data = new System.Windows.Forms.DataObject();
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(paths);
            data.SetFileDropList(list);
            data.SetText(string.Join(Environment.NewLine, paths), System.Windows.Forms.TextDataFormat.UnicodeText);
            const int DropEffectCopy = 1;
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(DropEffectCopy)));

            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(data, true, 10, 50);
                LastOwnSequence = Native.GetClipboardSequenceNumber();
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        /// <summary>
        /// Hedef pencereyi öne getirip Ctrl+V gönderir.
        /// Not: Yönetici olarak çalışan pencerelere, bu uygulama da yönetici değilse tuş gönderilemez (UIPI).
        /// </summary>
        public static async Task PasteIntoAsync(IntPtr targetWindow)
        {
            if (targetWindow != IntPtr.Zero && Native.IsWindow(targetWindow))
                Native.SetForegroundWindow(targetWindow);

            // Odağın gerçekten geçmesi için kısa bir bekleme.
            await Task.Delay(80);
            SendCtrlV();
        }

        private static void SendCtrlV()
        {
            var inputs = new[]
            {
                // Kullanıcının hâlâ basılı tutuyor olabileceği tuşları bırak, yoksa Ctrl+Shift+V gibi yanlış kombinasyon gider.
                Key(Native.VK_SHIFT, up: true),
                Key(Native.VK_MENU, up: true),
                Key(Native.VK_LWIN, up: true),
                Key(Native.VK_RWIN, up: true),

                Key(Native.VK_CONTROL, up: false),
                Key(Native.VK_V, up: false),
                Key(Native.VK_V, up: true),
                Key(Native.VK_CONTROL, up: true),
            };
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        }

        private static Native.INPUT Key(ushort vk, bool up) => new()
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = up ? Native.KEYEVENTF_KEYUP : 0
                }
            }
        };
    }
}
