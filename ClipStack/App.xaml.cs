using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
            StartedWithWindows = e.Args.Any(a => string.Equals(a, StartupArg, StringComparison.OrdinalIgnoreCase));

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
                MessageBox.Show(
                    "ClipStack başlatılamadı:\n\n" + ex.Message + "\n\nAyrıntılar: " + Log.FilePath,
                    "ClipStack", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
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
                MessageBox.Show("ClipStack zaten çalışıyor.", "ClipStack");
            }
        }

        private void Initialize()
        {
            Settings.Load();
            ThemeManager.Initialize(); // pencereler oluşmadan önce: renkler ilk çizimden itibaren doğru olsun
            StartupManager.RefreshPathIfEnabled();
            if (StartedWithWindows)
                Log.Info("Windows ile başlatıldı.");

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

            if (!_monitor.RegisterHotkey(Settings.Current.HotkeyModifiers, Settings.Current.HotkeyKey))
            {
                _tray!.ShowBalloonTip(5000, "ClipStack",
                    $"{Settings.HotkeyText} kısayolu başka bir uygulama tarafından kullanılıyor. " +
                    "Tepsi ikonuna çift tıklayarak açabilir, Seçenekler'den kısayolu değiştirebilirsin.",
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
                var answer = MessageBox.Show(
                    "Pano geçmişi açılamadı: şifreleme anahtarı bu Windows hesabında çözülemiyor ya da geçmiş başka bir " +
                    "anahtarla şifrelenmiş (ör. dosyalar başka bir bilgisayardan/hesaptan kopyalandı).\n\n" +
                    "Eski dosyalar silinmeden kenara alınıp boş bir geçmişle başlansın mı?\n" +
                    "\"Hayır\" derseniz ClipStack kapanır.",
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
            { } t when t == DateTime.MaxValue => "⏸ Kayıt duraklatıldı (sürdürülene kadar). Kopyalananlar geçmişe eklenmiyor.",
            { } t => $"⏸ Kayıt {t:HH:mm}'e kadar duraklatıldı. Kopyalananlar geçmişe eklenmiyor."
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
            _tray?.ShowBalloonTip(3000, "ClipStack", "Kayıt yeniden açıldı.", Forms.ToolTipIcon.Info);
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
                MessageBox.Show(
                    "Beklenmeyen bir hata oluştu, ClipStack çalışmaya devam ediyor:\n\n" + args.Exception.Message +
                    "\n\nAyrıntılar: " + Log.FilePath,
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
            menu.Items.Add("Geçmişi aç", null, (_, _) => ShowPopup(fromTray: true));
            menu.Items.Add("Seçenekler…", null, (_, _) => ShowOptions());
            menu.Items.Add(new Forms.ToolStripSeparator());

            // Hassas bir işlem öncesi (parola girme, gizli belge) kaydı geçici olarak kapatmak için.
            _pauseMenu = new Forms.ToolStripMenuItem("Kaydı duraklat");
            _pauseMenu.DropDownItems.Add("5 dakika", null, (_, _) => Pause(TimeSpan.FromMinutes(5)));
            _pauseMenu.DropDownItems.Add("15 dakika", null, (_, _) => Pause(TimeSpan.FromMinutes(15)));
            _pauseMenu.DropDownItems.Add("1 saat", null, (_, _) => Pause(TimeSpan.FromHours(1)));
            _pauseMenu.DropDownItems.Add("Ben sürdürene kadar", null, (_, _) => Pause(null));
            _resumeMenu = new Forms.ToolStripMenuItem("Kaydı sürdür", null, (_, _) => Resume()) { Visible = false };
            menu.Items.Add(_pauseMenu);
            menu.Items.Add(_resumeMenu);
            menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add("Çıkış", null, (_, _) => Shutdown());

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
                { } t when t == DateTime.MaxValue => "ClipStack – kayıt duraklatıldı",
                { } t => $"ClipStack – kayıt {t:HH:mm}'e kadar duraklatıldı"
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
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
