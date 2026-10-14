using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClipStack.Resources;
using Forms = System.Windows.Forms;

namespace ClipStack
{
    public partial class App : Application
    {
        /// <summary>Windows ile başlatılırken (HKCU Run kaydı) verilen argüman.</summary>
        public const string StartupArg = "--startup";

        private const string MutexName = "ClipStack.SingleInstance";

        /// <summary>İkinci örnek, çalışan örneğe "geçmişi aç" demek için bu olayı tetikler.</summary>
        private const string ShowEventName = "ClipStack.ShowPopup";

        private Mutex? _singleInstance;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _showWait;
        private ClipDatabase? _db;
        private ClipboardMonitor? _monitor;
        private ForegroundTracker? _foreground;
        private PopupWindow? _popup;
        private Forms.NotifyIcon? _tray;
        private OptionsWindow? _options;
        private DispatcherTimer? _hourlyTimer;

        /// <summary>Uygulama Windows açılışında mı başlatıldı.</summary>
        public bool StartedWithWindows { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            HookUnhandledExceptions();
            // Ayarlar okunmadan önce gösterilebilecek mesajlar ("zaten çalışıyor") için Windows dili;
            // ayarlardaki dil Initialize'da uygulanır.
            Localization.Apply(AppLanguage.System);
            StartedWithWindows = e.Args.Any(a => string.Equals(a, StartupArg, StringComparison.OrdinalIgnoreCase));
            WaitForPreviousInstance(e.Args);

            _singleInstance = new Mutex(true, MutexName, out bool isNew);
            if (!isNew)
            {
                HandOverToRunningInstance();
                Shutdown();
                return;
            }

            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                // Veritabanı kilitli/bozuk, pano dinleyicisi eklenemedi vb. Sessizce kapanmak yerine söyle.
                Log.Error("Başlatma başarısız", ex);
                MessageBox.Show(Localization.F(Strings.App_StartFailed, ex.Message, Log.FilePath),
                    "ClipStack", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>Yeniden başlatmada yeni örneğe verilen argüman: "--after &lt;eski süreç no&gt;".</summary>
        public const string AfterArg = "--after";

        /// <summary>
        /// Yeniden başlatma (dil değişikliği) sırasında eski örnek kapanmadan yeni örnek açılırsa, tek örnek
        /// kontrolü onu "ikinci örnek" sanıp kapatırdı. Bu yüzden yeni örnek, eskisinin çıkmasını bekler.
        /// </summary>
        private static void WaitForPreviousInstance(string[] args)
        {
            int? pid = ParseAfterArg(args);
            if (pid == null) return;
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(pid.Value);
                previous.WaitForExit(10_000);
            }
            catch (ArgumentException)
            {
                // Zaten kapanmış.
            }
        }

        internal static int? ParseAfterArg(string[] args)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, AfterArg, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int pid) && pid > 0 ? pid : null;
        }

        /// <summary>Uygulamayı yeniden başlatır (ör. dil değişince). Yeni örnek bu süreç kapanınca açılır.</summary>
        public void Restart()
        {
            Log.Info("Yeniden başlatılıyor (dil değişikliği).");
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                {
                    ArgumentList = { AfterArg, Environment.ProcessId.ToString() },
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                Log.Error("Yeniden başlatılamadı", ex);
                return; // açık kalmak, hiç açılmamaktan iyidir
            }
            Shutdown();
        }

        /// <summary>
        /// ClipStack zaten çalışıyor. Windows açılışındaysa sessizce çık (kullanıcı bir şey istemedi);
        /// kullanıcı exe'yi elle açtıysa çalışan örneğin geçmiş penceresini aç.
        /// </summary>
        private void HandOverToRunningInstance()
        {
            if (StartedWithWindows)
                return;

            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
            {
                using (ev)
                {
                    // Ön plana geçme hakkı şu an bizde (kullanıcı bizi açtı); çalışan örneğe devret.
                    Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                    ev.Set();
                }
            }
            else
            {
                // Olayı tanımayan eski bir sürüm çalışıyor.
                MessageBox.Show(Strings.App_AlreadyRunning, "ClipStack");
            }
        }

        private void Initialize()
        {
            // Her oturumun ilk satırı: sonraki hataların hangi sürüme/ortama ait olduğu log'dan anlaşılsın.
            Log.Info(SessionDescription());

            Settings.Load();
            Localization.Apply(Settings.Current.Language); // pencereler ve tepsi menüsü oluşmadan önce
            ThemeManager.Initialize(); // pencereler oluşmadan önce: renkler ilk çizimden itibaren doğru olsun
            StartupManager.RefreshPathIfEnabled();

            _db = OpenDatabase();
            _db.ApplyLimits(); // kapalıyken süresi dolanları hemen temizle
            _popup = new PopupWindow(_db) { PauseInfo = () => PauseDescription };

            // Hiç kopyalama yapılmasa da süre dolumu ve otomatik yedek işlesin diye saatte bir kontrol.
            _hourlyTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            _hourlyTimer.Tick += (_, _) =>
            {
                _db.ApplyLimits();
                RunAutoBackupIfEnabled();
            };
            _hourlyTimer.Start();

            _foreground = new ForegroundTracker();

            _monitor = new ClipboardMonitor();
            RawClipboard.Owner = _monitor.Handle;
            _monitor.TextCopied += (text, html, rtf, app) => { _db.AddOrTouch(text, html, rtf, app); _popup.OnHistoryChanged(); };
            _monitor.ImageCopied += OnImageCopied;
            _monitor.FilesCopied += (files, app) => { _db.AddFiles(files, app); _popup.OnHistoryChanged(); };
            _monitor.HotkeyPressed += () => ShowPopup(fromTray: false);

            CreateTray();

            if (Settings.LoadProblem != null)
                _tray!.ShowBalloonTip(8000, "ClipStack",
                    Settings.LoadProblem + "\n" + Localization.F(Strings.App_Details, Log.FilePath),
                    Forms.ToolTipIcon.Warning);

            if (!_monitor.RegisterHotkey(Settings.Current.HotkeyModifiers, Settings.Current.HotkeyKey))
            {
                _tray!.ShowBalloonTip(5000, "ClipStack", Localization.F(Strings.App_HotkeyInUse, Settings.HotkeyText),
                    Forms.ToolTipIcon.Warning);
            }

            ListenForSecondInstance();

            // Eski sürümden kalan şifresiz otomatik yedekleri şifrele, sonra (açıksa) günlük yedeği al.
            // Aynı klasörde çalıştıkları için sırayla, arka planda.
            var db = _db;
            Task.Run(() =>
            {
                int n = db.EncryptPlaintextBackups(Settings.BackupFolder);
                if (n > 0) Log.Info($"{n} eski yedek şifrelendi.");
            }).ContinueWith(_ => Dispatcher.BeginInvoke(RunAutoBackupIfEnabled));
        }

        /// <summary>
        /// Şifreli geçmişi açar. Anahtar dosyası bu hesapta çözülemiyorsa veya geçmiş başka bir anahtarla
        /// şifrelenmişse, eski dosyaları kenara alıp boş geçmişle başlamayı önerir.
        /// </summary>
        private ClipDatabase OpenDatabase()
        {
            try
            {
                byte[] key = DatabaseKey.LoadOrCreate(DatabaseKey.DefaultPath);
                return new ClipDatabase(Settings.DatabasePath, key);
            }
            catch (Exception ex) when (ex is DatabaseKeyException or System.Security.Cryptography.CryptographicException)
            {
                Log.Error("Geçmiş açılamadı (anahtar)", ex);
                var answer = MessageBox.Show(Strings.App_KeyProblem,
                    "ClipStack", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                    throw;

                string suffix = ".okunamayan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                foreach (string f in new[] { Settings.DatabasePath, Settings.DatabasePath + "-wal",
                                             Settings.DatabasePath + "-shm", DatabaseKey.DefaultPath })
                {
                    if (System.IO.File.Exists(f))
                        System.IO.File.Move(f, f + suffix);
                }
                Log.Info("Okunamayan geçmiş kenara alındı (" + suffix + "), yeni geçmiş oluşturuluyor.");
                return new ClipDatabase(Settings.DatabasePath, DatabaseKey.LoadOrCreate(DatabaseKey.DefaultPath));
            }
        }

        // ---------- Kaydı duraklat ----------

        private DateTime? _pausedUntil; // null: kayıt açık · DateTime.MaxValue: sürdürülene kadar
        private DispatcherTimer? _pauseTimer;
        private Forms.ToolStripMenuItem? _pauseMenu;
        private Forms.ToolStripMenuItem? _resumeMenu;
        private System.Drawing.Icon? _pausedIcon;

        public bool IsPaused => _pausedUntil != null;

        /// <summary>Duraklatma durumunun kısa açıklaması; kayıt açıksa null.</summary>
        public string? PauseDescription => _pausedUntil switch
        {
            null => null,
            { } t when t == DateTime.MaxValue => Strings.Pause_UntilResumed,
            { } t => Localization.F(Strings.Pause_Until, t.ToString("t", Localization.FormatCulture))
        };

        /// <summary>Kaydı belirtilen süre (null: sürdürülene kadar) duraklatır.</summary>
        public void Pause(TimeSpan? duration)
        {
            _pausedUntil = duration.HasValue ? DateTime.Now + duration.Value : DateTime.MaxValue;
            _monitor!.Paused = true;
            if (_pauseTimer == null)
            {
                _pauseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                _pauseTimer.Tick += (_, _) =>
                {
                    if (_pausedUntil != null && _pausedUntil != DateTime.MaxValue && DateTime.Now >= _pausedUntil)
                        Resume();
                };
            }
            _pauseTimer.Start();
            Log.Info("Kayıt duraklatıldı" + (duration.HasValue ? $" ({duration.Value.TotalMinutes:0} dk)." : " (sürdürülene kadar)."));
            UpdatePauseUi();
        }

        public void Resume()
        {
            _pausedUntil = null;
            _monitor!.Paused = false;
            _pauseTimer?.Stop();
            Log.Info("Kayıt sürdürüldü.");
            UpdatePauseUi();
            _tray?.ShowBalloonTip(3000, "ClipStack", Strings.Pause_Resumed, Forms.ToolTipIcon.Info);
        }

        private void UpdatePauseUi()
        {
            if (_tray == null) return;
            _tray.Icon = IsPaused ? (_pausedIcon ??= MakePausedIcon()) : TrayIcon;
            UpdateTrayText(Settings.HotkeyText);
            _pauseMenu!.Visible = !IsPaused;
            _resumeMenu!.Visible = IsPaused;
        }

        private System.Drawing.Icon? _trayIcon;

        /// <summary>
        /// Tepsi ikonu: Assets\ClipStack.ico'dan sistemin küçük ikon boyutuna (DPI'a göre 16/20/24 px)
        /// en uygun kare. Okunamazsa Windows'un genel uygulama ikonu.
        /// </summary>
        private System.Drawing.Icon TrayIcon => _trayIcon ??= LoadTrayIcon();

        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var info = GetResourceStream(new Uri("pack://application:,,,/Assets/ClipStack.ico"));
                if (info != null)
                {
                    using var stream = info.Stream;
                    return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Tepsi ikonu yüklenemedi", ex);
            }
            return System.Drawing.SystemIcons.Application;
        }

        /// <summary>Uygulama ikonunun köşesine turuncu "duraklat" işareti çizer.</summary>
        private System.Drawing.Icon MakePausedIcon()
        {
            var size = Forms.SystemInformation.SmallIconSize;
            using var bmp = new System.Drawing.Bitmap(size.Width, size.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawIcon(TrayIcon, new System.Drawing.Rectangle(0, 0, size.Width, size.Height));
                int d = size.Width * 5 / 8;
                var badge = new System.Drawing.Rectangle(size.Width - d, size.Height - d, d, d);
                using var orange = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(232, 120, 20));
                g.FillEllipse(orange, badge);
                int barW = Math.Max(1, d / 6), barH = d / 2;
                int top = badge.Top + (d - barH) / 2;
                int cx = badge.Left + d / 2;
                g.FillRectangle(System.Drawing.Brushes.White, cx - barW - barW / 2 - 1, top, barW, barH);
                g.FillRectangle(System.Drawing.Brushes.White, cx + barW / 2 + 1, top, barW, barH);
            }
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        /// <summary>
        /// Görseli arka planda hazırlar (PNG'ye çevirme, küçük resim, özet; büyük ekran görüntülerinde yüzlerce ms),
        /// sonra yine arka planda veritabanına yazar (20 MB şifreleyip yazmak yarım saniyeyi bulur); arayüz iş parçacığında
        /// sadece sınırları uygulama ve listeyi yenileme kalır. Böylece büyük bir görsel kopyalamak pencereleri dondurmaz.
        /// </summary>
        private async void OnImageCopied(ClipboardImage image, string? app)
        {
            try
            {
                PreparedImage? prepared;
                using (image)
                    prepared = await Task.Run(() => ImageProcessing.Prepare(image));
                if (prepared == null) return; // çok büyük ya da okunamadı
                await _db!.AddImageInBackgroundAsync(prepared, app);
                _db.ApplyLimits();
                _popup!.OnHistoryChanged();
            }
            catch (Exception ex)
            {
                Log.Error("Görsel kaydedilemedi", ex);
            }
        }

        /// <summary>Örn. "ClipStack 0.4.0 başlatıldı · Windows ile · .NET 8.0.31 · Microsoft Windows 10.0.19045 · x64 · %125 · PID 1234".</summary>
        internal string SessionDescription()
        {
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
            var parts = new System.Collections.Generic.List<string> { $"ClipStack {version} başlatıldı" };
            if (StartedWithWindows) parts.Add("Windows ile");
            parts.Add(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            parts.Add(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            parts.Add(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
            try
            {
                Native.GetCursorPos(out var p);
                uint dpi = WindowPlacement.MonitorFromPoint(p.X, p.Y).Dpi;
                parts.Add($"ekran ölçeği %{dpi * 100 / 96}");
            }
            catch { /* ölçek bilgisi isteğe bağlı */ }
            parts.Add($"PID {Environment.ProcessId}");
            return string.Join(" · ", parts);
        }

        private void ListenForSecondInstance()
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => Dispatcher.BeginInvoke(() => ShowPopup(fromTray: true)),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }

        /// <summary>
        /// Kısayolu değiştirir. Yeni kısayol başka uygulamada kullanılıyorsa eskisini geri kaydeder ve false döner.
        /// Ayarları kaydetmek çağıranın işidir.
        /// </summary>
        public bool TryChangeHotkey(uint modifiers, uint key)
        {
            var s = Settings.Current;
            if (modifiers == s.HotkeyModifiers && key == s.HotkeyKey)
                return true;

            if (_monitor!.RegisterHotkey(modifiers, key))
            {
                UpdateTrayText(Settings.FormatHotkey(modifiers, key));
                return true;
            }
            _monitor.RegisterHotkey(s.HotkeyModifiers, s.HotkeyKey);
            return false;
        }

        /// <summary>Otomatik yedek açıksa ve son yedek bir günden eskiyse arka planda yedek alır.</summary>
        public void RunAutoBackupIfEnabled()
        {
            if (!Settings.Current.AutoBackup || _db == null) return;
            var db = _db;
            Task.Run(() =>
            {
                try
                {
                    string? path = db.AutoBackupIfDue(Settings.BackupFolder, TimeSpan.FromDays(1), Settings.AutoBackupKeep);
                    if (path != null) Log.Info("Otomatik yedek alındı: " + path);
                }
                catch (Exception ex)
                {
                    Log.Error("Otomatik yedek alınamadı", ex);
                }
            });
        }

        /// <summary>
        /// Arayüz iş parçacığındaki hatalar uygulamayı kapatmasın; kaydedilip kullanıcıya gösterilir.
        /// Diğer iş parçacıklarındaki hatalar sürecin kapanmasını engelleyemez ama en azından log'a yazılır.
        /// </summary>
        private void HookUnhandledExceptions()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                Log.Error("Beklenmeyen hata (arayüz)", args.Exception);
                args.Handled = true;
                MessageBox.Show(Localization.F(Strings.App_UnexpectedError, args.Exception.Message, Log.FilePath),
                    "ClipStack", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Error("Beklenmeyen hata (kritik)", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error("Gözlemlenmeyen görev hatası", args.Exception);
                args.SetObserved();
            };
        }

        private void ShowPopup(bool fromTray)
        {
            // Popup açılmadan ÖNCE hedef pencereyi belirle; yapıştırma oraya yapılacak.
            // Tepsiden açılırken ön pencere görev çubuğudur, o yüzden son gerçek uygulama penceresi kullanılır.
            IntPtr target = fromTray
                ? _foreground!.LastWindow
                : _foreground!.ResolveTarget(Native.GetForegroundWindow());
            // Kısayola açıkken tekrar basmak pencereyi kapatır; tepsi/ikinci örnek ise sadece öne getirir.
            _popup!.ShowAt(target, toggle: !fromTray);
        }

        /// <summary>Seçenekler penceresini açar; zaten açıksa öne getirir.</summary>
        public void ShowOptions()
        {
            if (_options != null)
            {
                if (_options.WindowState == WindowState.Minimized)
                    _options.WindowState = WindowState.Normal;
                _options.Activate();
                return;
            }
            _options = new OptionsWindow(_db!);
            _options.Closed += (_, _) => _options = null;
            _options.Show();
            _options.Activate();
        }

        private void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(Strings.Tray_Open, null, (_, _) => ShowPopup(fromTray: true));
            menu.Items.Add(Strings.Tray_Options, null, (_, _) => ShowOptions());
            menu.Items.Add(new Forms.ToolStripSeparator());

            // Hassas bir işlem öncesi (parola girme, gizli belge) kaydı geçici olarak kapatmak için.
            _pauseMenu = new Forms.ToolStripMenuItem(Strings.Tray_Pause);
            _pauseMenu.DropDownItems.Add(Strings.Tray_Pause5, null, (_, _) => Pause(TimeSpan.FromMinutes(5)));
            _pauseMenu.DropDownItems.Add(Strings.Tray_Pause15, null, (_, _) => Pause(TimeSpan.FromMinutes(15)));
            _pauseMenu.DropDownItems.Add(Strings.Tray_Pause60, null, (_, _) => Pause(TimeSpan.FromHours(1)));
            _pauseMenu.DropDownItems.Add(Strings.Tray_PauseIndefinitely, null, (_, _) => Pause(null));
            _resumeMenu = new Forms.ToolStripMenuItem(Strings.Tray_Resume, null, (_, _) => Resume()) { Visible = false };
            menu.Items.Add(_pauseMenu);
            menu.Items.Add(_resumeMenu);
            menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add(Strings.Tray_Exit, null, (_, _) => Shutdown());

            _tray = new Forms.NotifyIcon
            {
                Icon = TrayIcon,
                ContextMenuStrip = menu,
                Visible = true
            };
            UpdateTrayText(Settings.HotkeyText);
            _tray.DoubleClick += (_, _) => ShowPopup(fromTray: true);
        }

        private void UpdateTrayText(string hotkeyText)
        {
            if (_tray == null) return;
            _tray.Text = _pausedUntil switch
            {
                null => $"ClipStack ({hotkeyText})",
                { } t when t == DateTime.MaxValue => Strings.Tray_PausedIndefinitely,
                { } t => Localization.F(Strings.Tray_PausedUntil, t.ToString("t", Localization.FormatCulture))
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Bir oturumun başlangıç satırı olup kapanış satırı yoksa uygulama çökmüş/zorla kapatılmış demektir.
            if (_db != null)
                Log.Info($"ClipStack kapatıldı (çıkış kodu {e.ApplicationExitCode}).");
            _hourlyTimer?.Stop();
            ThemeManager.Shutdown();
            _pauseTimer?.Stop();
            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _pausedIcon?.Dispose();
            _trayIcon?.Dispose();
            _monitor?.Dispose();
            _foreground?.Dispose();
            _db?.Dispose();
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
