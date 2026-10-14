using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClipStack
{
    /// <summary>
    /// Görünmez (message-only) bir pencere üzerinden pano değişikliklerini ve global kısayolu dinler.
    ///
    /// Pano içeriği ayrı bir STA iş parçacığında okunur. Neden: kopyalayan uygulama veriyi "geciktirilmiş"
    /// verebilir (istenince üretir) ya da büyük bir görseli yazmayı henüz bitirmemiş olabilir; okuma bu sırada
    /// saniyelerce bekleyebilir. Bu bekleme arayüz iş parçacığında olsaydı tüm pencereler donardı.
    /// Olaylar (TextCopied vb.) yine arayüz iş parçacığında, monitörü oluşturan iş parçacığında tetiklenir.
    /// </summary>
    public sealed class ClipboardMonitor : IDisposable
    {
        private const int HotkeyId = 0xC11B;

        private readonly HwndSource _source;
        private readonly uint _excludeFormat;
        private readonly uint _canIncludeFormat;
        private readonly uint _viewerIgnoreFormat;
        private readonly Dispatcher _ui;
        private readonly Dispatcher _reader;
        private bool _hotkeyRegistered;

        /// <summary>
        /// En son bildirilen pano sıra numarası. Okuma sırasında daha yenisi gelirse eski okuma atılır; böylece
        /// tek bir kopyalama için gelen art arda bildirimler (ör. önce boş, sonra dolu veri) çift kayıt üretmez.
        /// </summary>
        private uint _latestSequence;

        /// <summary>
        /// Yeni bir metin panoya kopyalandığında: (metin, HTML baytları, RTF baytları, kaynak uygulama adı).
        /// Biçim yoksa (ör. Not Defteri) HTML ve RTF null gelir.
        /// </summary>
        public event Action<string, byte[]?, byte[]?, string?>? TextCopied;

        /// <summary>Panoya görsel kopyalandığında: (panodaki görsel, kaynak uygulama adı).</summary>
        /// <remarks>Görselin sahipliği dinleyiciye geçer; işi bitince Dispose etmelidir.</remarks>
        public event Action<ClipboardImage, string?>? ImageCopied;

        /// <summary>Gezgin vb.'den dosya kopyalandığında: (tam yollar, kaynak uygulama adı).</summary>
        public event Action<string[], string?>? FilesCopied;

        /// <summary>Global kısayola basıldığında.</summary>
        public event Action? HotkeyPressed;

        public ClipboardMonitor()
        {
            var p = new HwndSourceParameters("ClipStackMonitor")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = Native.HWND_MESSAGE
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);

            // Parola yöneticileri ve hassas uygulamalar bu formatlarla "beni kaydetme" der.
            _excludeFormat = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
            _canIncludeFormat = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
            _viewerIgnoreFormat = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");

            _ui = Dispatcher.CurrentDispatcher;
            _reader = StartReaderThread();

            if (!Native.AddClipboardFormatListener(_source.Handle))
                throw new InvalidOperationException("Pano dinleyicisi eklenemedi. Hata: " + Marshal.GetLastWin32Error());
        }

        /// <summary>Pano okumaları için kendi mesaj döngüsü olan STA iş parçacığı (OLE pano erişimi STA ister).</summary>
        private static Dispatcher StartReaderThread()
        {
            Dispatcher? dispatcher = null;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                Name = "ClipStack pano okuyucu",
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return dispatcher!;
        }

        public IntPtr Handle => _source.Handle;

        /// <summary>
        /// Kayıt duraklatıldı mı ("Kaydı duraklat"). Duraklatılmışken pano hiç okunmaz; kopyalananlar
        /// geçmişe girmez ve duraklatma bitince de sonradan eklenmez.
        /// </summary>
        public bool Paused { get; set; }

        /// <summary>Global kısayolu kaydeder. Başka uygulama aynı kısayolu aldıysa false döner.</summary>
        public bool RegisterHotkey(uint modifiers, uint virtualKey)
        {
            if (_hotkeyRegistered)
                Native.UnregisterHotKey(_source.Handle, HotkeyId);

            _hotkeyRegistered = Native.RegisterHotKey(_source.Handle, HotkeyId, modifiers | Native.MOD_NOREPEAT, virtualKey);
            return _hotkeyRegistered;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_CLIPBOARDUPDATE)
            {
                // Arayüz iş parçacığında sadece ucuz kontroller; okuma okuyucu iş parçacığına.
                uint sequence = Native.GetClipboardSequenceNumber();
                if (!Paused && sequence != PasteService.LastOwnSequence) // kendi yazdıklarımızı (geçmişten yapıştırma) sayma
                {
                    string? app = ResolveSourceApp();
                    Volatile.Write(ref _latestSequence, sequence);
                    _reader.BeginInvoke(() => ReadClipboard(sequence, app));
                }
                handled = true;
            }
            else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                HotkeyPressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>Okunan sonuç, okuma sırasında daha yeni bir pano değişikliği olmadıysa geçerlidir.</summary>
        private bool IsStale(uint sequence) => Volatile.Read(ref _latestSequence) != sequence;

        /// <summary>Okuma sonucunu arayüz iş parçacığında, hâlâ geçerliyse ve kayıt duraklatılmadıysa bildirir.</summary>
        private void Deliver(uint sequence, Action raise, IDisposable? payload = null)
        {
            if (IsStale(sequence))
            {
                payload?.Dispose();
                return;
            }
            _ui.BeginInvoke(() =>
            {
                if (Paused || IsStale(sequence))
                {
                    payload?.Dispose();
                    return;
                }
                raise();
            });
        }

        /// <summary>
        /// Bildirimden sonra bu kadar beklenir; bu sürede yeni bildirim gelirse eskisi okunmaz. Pek çok uygulama
        /// (OLE ile kopyalayan .NET/Office uygulamaları) tek kopyalamada art arda iki bildirim üretir; ikisini
        /// de işlemek çift sayım ve yanlış kaynak uygulama demekti.
        /// </summary>
        private const int SettleMs = 40;

        /// <summary>Okuyucu iş parçacığında çalışır.</summary>
        private void ReadClipboard(uint sequence, string? app)
        {
            try
            {
                Thread.Sleep(SettleMs);
                // Bu arada pano yine değiştiyse bu okumayı atla; yenisi zaten sırada.
                if (IsStale(sequence))
                    return;

                if (Native.IsClipboardFormatAvailable(_excludeFormat) ||
                    Native.IsClipboardFormatAvailable(_viewerIgnoreFormat) ||
                    IsHistoryDisallowed())
                    return;

                if (Settings.IsExcludedApp(app))
                    return;

                // Dosyalar önce: Gezgin'den kopyalanan dosyalarla birlikte bazen yolları içeren metin de
                // gelir; kullanıcı dosyayı kopyalamıştır, yapıştırırken de dosya olarak gelmesi gerekir.
                string[]? files = TryGetFiles();
                if (files != null && files.Length > 0)
                {
                    Deliver(sequence, () => FilesCopied?.Invoke(files, app));
                    return;
                }

                // Metin varsa öncelik metinde. (Excel hücreleri kopyalanınca hem metin hem görsel gelir;
                // kullanıcı neredeyse her zaman metni ister.)
                if (System.Windows.Clipboard.ContainsText())
                {
                    string? text = TryGetText();
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    // Tarayıcı parola yöneticisi, terminal vb. "beni kaydetme" demez; içeriğe de bak.
                    if (SensitiveContent.ShouldSkip(text, Settings.Current, out string reason))
                    {
                        Log.Info($"Hassas içerik kaydedilmedi ({reason}, kaynak: {app ?? "?"}).");
                        return;
                    }

                    byte[]? html = null, rtf = null;
                    if (text.Length > Settings.MaxTextLength)
                    {
                        // Kırpılan metinle biçimli hali artık uyuşmaz; biçimi bırak.
                        text = text.Substring(0, Settings.MaxTextLength);
                    }
                    else
                    {
                        html = RawClipboard.Read(RawClipboard.CF_HTML);
                        rtf = RawClipboard.Read(RawClipboard.CF_RTF);
                        if (html != null && html.Length > Settings.MaxFormatBytes) html = null;
                        if (rtf != null && rtf.Length > Settings.MaxFormatBytes) rtf = null;
                    }

                    string finalText = text;
                    Deliver(sequence, () => TextCopied?.Invoke(finalText, html, rtf, app));
                }
                else if (HasImage())
                {
                    var image = TryGetImage();
                    if (image != null)
                    {
                        Deliver(sequence, () =>
                        {
                            if (ImageCopied != null) ImageCopied.Invoke(image, app);
                            else image.Dispose();
                        }, payload: image);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Pano okunamadı veya kayıt eklenemedi", ex);
            }
        }

        /// <summary>"CanIncludeInClipboardHistory" = 0 ise içerik geçmişe alınmamalı.</summary>
        private bool IsHistoryDisallowed()
        {
            if (!Native.IsClipboardFormatAvailable(_canIncludeFormat))
                return false;
            try
            {
                if (System.Windows.Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
                {
                    var buf = new byte[4];
                    ms.Position = 0;
                    ms.Read(buf, 0, 4);
                    return BitConverter.ToInt32(buf, 0) == 0;
                }
            }
            catch { /* okunamazsa güvenli tarafta kal */ return true; }
            return false;
        }

        /// <summary>Pano başka bir süreç tarafından kilitliyse birkaç kez yeniden dener.</summary>
        private static string? TryGetText()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    return System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
                }
                catch (COMException)
                {
                    Thread.Sleep(30);
                }
            }
            return null;
        }

        /// <summary>Panoda kopyalanmış dosya listesi (CF_HDROP) varsa yollarını döndürür.</summary>
        private static string[]? TryGetFiles()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (!Forms.Clipboard.ContainsFileDropList())
                        return null;
                    var list = Forms.Clipboard.GetFileDropList();
                    var result = new string[list.Count];
                    list.CopyTo(result, 0);
                    return result;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(30);
                }
            }
            return null;
        }

        private static bool HasImage()
        {
            try
            {
                return Forms.Clipboard.ContainsImage() || Forms.Clipboard.ContainsData("PNG");
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        /// <summary>
        /// Panodaki görseli okur. Önce "PNG" formatını dener (tarayıcılar, Office ve Ekran Alıntısı Aracı bunu verir,
        /// şeffaflık korunur); yoksa klasik bitmap'i (CF_DIB) alır. Bitmap → PNG çevirisi büyük görsellerde yavaş
        /// olduğu için burada yapılmaz; arayüz iş parçacığında sadece panodan okuma yapılır, çeviri arka planda olur.
        /// WPF'in Clipboard.GetImage() metodu bazı uygulamalarda siyah/bozuk görsel döndürdüğü için WinForms kullanılır.
        /// </summary>
        private static ClipboardImage? TryGetImage()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    var data = Forms.Clipboard.GetDataObject();
                    if (data == null) return null;

                    if (data.GetDataPresent("PNG") && data.GetData("PNG") is Stream pngStream)
                    {
                        using var ms = new MemoryStream();
                        if (pngStream.CanSeek) pngStream.Position = 0;
                        pngStream.CopyTo(ms);
                        if (ms.Length > 0) return new ClipboardImage(ms.ToArray());
                    }

                    using Drawing.Image? img = Forms.Clipboard.GetImage();
                    // Panodaki nesneye bağlı kalmasın diye bağımsız bir kopya; sahipliği ClipboardImage'a geçer.
                    return img == null ? null : new ClipboardImage(new Drawing.Bitmap(img));
                }
                catch (ExternalException)
                {
                    Thread.Sleep(30);
                }
            }
            return null;
        }

        private string? _lastOwnerApp;
        private long _lastOwnerTick;

        /// <summary>
        /// Kopyalayan uygulamayı bildirim anında (arayüz iş parçacığında) belirler. Pano sahibi penceresi yoksa:
        /// az önce bir sahip görüldüyse aynı kopyalamanın ikinci bildirimidir, o uygulama kullanılır (OLE panoyu
        /// "flush" edince sahip penceresi kaybolur); yoksa o an önde olan pencerenin uygulaması.
        /// </summary>
        private string? ResolveSourceApp()
        {
            long now = Environment.TickCount64;
            IntPtr owner = Native.GetClipboardOwner();
            if (owner != IntPtr.Zero)
            {
                string? app = ProcessNameOf(owner);
                if (app != null)
                {
                    _lastOwnerApp = app;
                    _lastOwnerTick = now;
                    return app;
                }
            }
            if (_lastOwnerApp != null && now - _lastOwnerTick < 1000)
                return _lastOwnerApp;
            return ProcessNameOf(Native.GetForegroundWindow());
        }

        private static string? ProcessNameOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return null;
            try
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return null;
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_hotkeyRegistered)
                Native.UnregisterHotKey(_source.Handle, HotkeyId);
            Native.RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _reader.BeginInvokeShutdown(DispatcherPriority.Normal);
        }
    }
}
