using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClipStack
{
    public partial class OptionsWindow : Window
    {
        private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
        private readonly ClipDatabase _db;
        private bool _ready;

        /// <summary>Kısayol kutusunda yakalanan, henüz uygulanmamış kısayol.</summary>
        private uint _pendingModifiers;
        private uint _pendingKey;

        public OptionsWindow(ClipDatabase db)
        {
            InitializeComponent();
            _db = db;
            ThemeManager.ApplyTitleBar(this);

            // Sayı kutularına sadece rakam girilebilsin.
            foreach (var box in new[] { MaxHistoryBox, MaxImagesBox, ExpireDaysBox })
            {
                box.PreviewTextInput += (_, e) => e.Handled = !IsDigits(e.Text);
                DataObject.AddPastingHandler(box, (_, e) =>
                {
                    if (e.DataObject.GetData(typeof(string)) is not string s || !IsDigits(s.Trim()))
                        e.CancelCommand();
                });
            }

            LoadGeneral();
            LoadHotkeyAndPrivacy();
            LoadBackup();
            LoadAbout();
            _ready = true;
        }

        private App App => (App)Application.Current;

        // ---------- Genel ----------

        private void LoadGeneral()
        {
            var s = Settings.Current;
            MaxHistoryBox.Text = s.MaxHistory.ToString(CultureInfo.InvariantCulture);
            MaxImagesBox.Text = s.MaxImages.ToString(CultureInfo.InvariantCulture);
            ExpireDaysBox.Text = s.ExpireDays.ToString(CultureInfo.InvariantCulture);
            ExpireCheck.IsChecked = s.ExpireEnabled;
            ExpireDaysBox.IsEnabled = s.ExpireEnabled;

            try
            {
                StartupCheck.IsChecked = StartupManager.IsEnabled();
            }
            catch
            {
                StartupCheck.IsChecked = false;
                StartupCheck.IsEnabled = false;
            }
            StartupPathText.Text = "Çalıştırılacak dosya: " + StartupManager.ExePath;

            ThemeSystem.IsChecked = s.Theme == AppTheme.System;
            ThemeLight.IsChecked = s.Theme == AppTheme.Light;
            ThemeDark.IsChecked = s.Theme == AppTheme.Dark;
            PlaceCursor.IsChecked = s.PopupPlacement == PopupPlacement.Cursor;
            PlaceRemember.IsChecked = s.PopupPlacement == PopupPlacement.Remember;
            PlaceCenter.IsChecked = s.PopupPlacement == PopupPlacement.Center;
        }

        private AppTheme SelectedTheme =>
            ThemeLight.IsChecked == true ? AppTheme.Light : ThemeDark.IsChecked == true ? AppTheme.Dark : AppTheme.System;

        private PopupPlacement SelectedPlacement =>
            PlaceRemember.IsChecked == true ? PopupPlacement.Remember
            : PlaceCenter.IsChecked == true ? PopupPlacement.Center : PopupPlacement.Cursor;

        private void ExpireCheck_Changed(object sender, RoutedEventArgs e)
        {
            // Olay InitializeComponent sırasında, kutu henüz oluşmadan da tetiklenebilir.
            if (ExpireDaysBox != null)
                ExpireDaysBox.IsEnabled = ExpireCheck.IsChecked == true;
        }

        // ---------- Kısayol ve gizlilik ----------

        private void LoadHotkeyAndPrivacy()
        {
            _pendingModifiers = Settings.Current.HotkeyModifiers;
            _pendingKey = Settings.Current.HotkeyKey;
            HotkeyBox.Text = Settings.FormatHotkey(_pendingModifiers, _pendingKey);
            ExcludedAppsBox.Text = string.Join(Environment.NewLine, Settings.Current.ExcludedApps);
            SkipSecretsCheck.IsChecked = Settings.Current.SkipSecrets;
            SkipPasswordsCheck.IsChecked = Settings.Current.SkipPasswordLike;
        }

        private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            HotkeyBox.Text = "Tuş birleşimine bas…";
        }

        private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            HotkeyBox.Text = Settings.FormatHotkey(_pendingModifiers, _pendingKey);
        }

        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true; // Tab dışında hiçbir tuş kutuya ya da Tamam/İptal'e gitmesin
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Tab)
            {
                e.Handled = false;
                return;
            }
            if (key == Key.Escape)
            {
                Keyboard.ClearFocus();
                return;
            }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                HotkeyBox.Text = Settings.FormatModifiers(ToModFlags(Keyboard.Modifiers)) + "+…";
                return;
            }

            uint mods = ToModFlags(Keyboard.Modifiers);
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (!Settings.IsValidHotkey(mods, vk))
            {
                HotkeyBox.Text = "Ctrl veya Alt ile birlikte bas…";
                return;
            }
            _pendingModifiers = mods;
            _pendingKey = vk;
            HotkeyBox.Text = Settings.FormatHotkey(mods, vk);
        }

        private static uint ToModFlags(ModifierKeys m)
        {
            uint flags = 0;
            if ((m & ModifierKeys.Control) != 0) flags |= Native.MOD_CONTROL;
            if ((m & ModifierKeys.Alt) != 0) flags |= Native.MOD_ALT;
            if ((m & ModifierKeys.Shift) != 0) flags |= Native.MOD_SHIFT;
            if ((m & ModifierKeys.Windows) != 0) flags |= Native.MOD_WIN;
            return flags;
        }

        private void HotkeyDefault_Click(object sender, RoutedEventArgs e)
        {
            _pendingModifiers = Settings.DefaultHotkeyModifiers;
            _pendingKey = Settings.DefaultHotkeyKey;
            HotkeyBox.Text = Settings.FormatHotkey(_pendingModifiers, _pendingKey);
        }

        // ---------- Uygula ----------

        /// <summary>Girişleri doğrular, kaydeder ve hemen uygular. Hata varsa false döner.</summary>
        private bool ApplyAll()
        {
            if (!TryReadInt(MaxHistoryBox, Settings.MinHistory, Settings.MaxHistoryLimit,
                    "Kaydedilecek en fazla kopya sayısı", out int maxHistory))
                return false;
            if (!TryReadInt(MaxImagesBox, Settings.MinImages, Settings.MaxImagesLimit,
                    "Görsel sınırı", out int maxImages))
                return false;

            bool expire = ExpireCheck.IsChecked == true;
            int expireDays = Settings.Current.ExpireDays;
            if (expire && !TryReadInt(ExpireDaysBox, Settings.MinExpireDays, Settings.MaxExpireDays,
                    "Silme süresi", out expireDays))
                return false;

            var s = Settings.Current;
            bool shrinking = maxHistory < s.MaxHistory || maxImages < s.MaxImages ||
                             (expire && (!s.ExpireEnabled || expireDays < s.ExpireDays));
            if (shrinking)
            {
                var r = MessageBox.Show(this,
                    "Yeni ayarlar bazı eski kayıtların hemen silinmesine yol açabilir. Sabitlenen kayıtlar korunur.\n\nDevam edilsin mi?",
                    "ClipStack", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes)
                    return false;
            }

            // Kısayol: önce gerçekten kaydedilebiliyor mu dene; başka uygulama kullanıyorsa ayarı değiştirme.
            if (!App.TryChangeHotkey(_pendingModifiers, _pendingKey))
            {
                HotkeyTab.IsSelected = true;
                MessageBox.Show(this,
                    $"{Settings.FormatHotkey(_pendingModifiers, _pendingKey)} kısayolu başka bir uygulama tarafından kullanılıyor. " +
                    "Başka bir birleşim seç.",
                    "ClipStack", MessageBoxButton.OK, MessageBoxImage.Warning);
                HotkeyBox.Focus();
                return false;
            }

            s.MaxHistory = maxHistory;
            s.MaxImages = maxImages;
            s.ExpireEnabled = expire;
            s.ExpireDays = expireDays;
            s.HotkeyModifiers = _pendingModifiers;
            s.HotkeyKey = _pendingKey;
            s.ExcludedApps = Settings.NormalizeAppList(ExcludedAppsBox.Text.Split('\n'));
            s.SkipSecrets = SkipSecretsCheck.IsChecked == true;
            s.SkipPasswordLike = SkipPasswordsCheck.IsChecked == true;
            bool autoBackupTurnedOn = AutoBackupCheck.IsChecked == true && !s.AutoBackup;
            s.AutoBackup = AutoBackupCheck.IsChecked == true;
            bool themeChanged = s.Theme != SelectedTheme;
            s.Theme = SelectedTheme;
            s.PopupPlacement = SelectedPlacement;

            try
            {
                Settings.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Ayarlar kaydedilemedi:\n" + ex.Message, "ClipStack",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            _db.ApplyLimits();
            if (autoBackupTurnedOn)
                App.RunAutoBackupIfEnabled();
            if (themeChanged)
                ThemeManager.Apply(s.Theme);

            if (StartupCheck.IsEnabled)
            {
                bool wanted = StartupCheck.IsChecked == true;
                try
                {
                    if (wanted != StartupManager.IsEnabled())
                        StartupManager.SetEnabled(wanted);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "\"Windows ile başlat\" ayarı değiştirilemedi:\n" + ex.Message, "ClipStack",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                // Kayıt defterinin gerçek durumunu göster.
                StartupCheck.IsChecked = StartupManager.IsEnabled();
            }

            // Normalleşmiş halleri göster (".exe" kırpılmış, boş satırlar atılmış).
            LoadHotkeyAndPrivacy();
            LoadAbout();
            if (StatsTab.IsSelected) LoadStats();
            return true;
        }

        private bool TryReadInt(TextBox box, int min, int max, string name, out int value)
        {
            if (int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
                value >= min && value <= max)
                return true;

            GeneralTab.IsSelected = true;
            MessageBox.Show(this,
                $"{name} {min.ToString("N0", Tr)} ile {max.ToString("N0", Tr)} arasında bir sayı olmalı.",
                "ClipStack", MessageBoxButton.OK, MessageBoxImage.Warning);
            box.Focus();
            box.SelectAll();
            return false;
        }

        private static bool IsDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (c < '0' || c > '9') return false;
            return true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (ApplyAll())
                Close();
        }

        private void Apply_Click(object sender, RoutedEventArgs e) => ApplyAll();

        /// <summary>
        /// Kaydedilmemiş değişiklikleri bırakıp pencereyi kapatır. (IsCancel tek başına sadece
        /// ShowDialog ile açılan pencereleri kapatır; bu pencere Show ile açıldığı için gerekli.)
        /// Esc tuşu da IsCancel sayesinde bu düğmeyi tetikler.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        // ---------- İstatistikler ----------

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Sadece sekme değişiminde yenile; içteki kontrollerden kabaran olayları yok say.
            if (!_ready || e.OriginalSource != Tabs) return;
            if (StatsTab.IsSelected) LoadStats();
            if (BackupTab.IsSelected) LoadBackupInfo();
        }

        private void LoadStats()
        {
            var st = _db.GetStats();
            StatSince.Text = st.Since.ToString("dd.MM.yyyy HH:mm", Tr);
            StatCopies.Text = st.TotalCopies.ToString("N0", Tr);
            StatPastes.Text = st.TotalPastes.ToString("N0", Tr);
            StatToday.Text = st.AddedToday.ToString("N0", Tr);
            StatItems.Text = $"{st.Items.ToString("N0", Tr)}  (sınır: {Settings.Current.MaxHistory.ToString("N0", Tr)} + sabitlenenler)";
            StatKinds.Text = $"{st.Texts.ToString("N0", Tr)} / {st.Images.ToString("N0", Tr)} / {st.Files.ToString("N0", Tr)}" +
                             $"  (biçimli metin: {st.Formatted.ToString("N0", Tr)})";
            StatPinned.Text = st.Pinned.ToString("N0", Tr);
            StatOldest.Text = st.Oldest?.ToString("dd.MM.yyyy HH:mm", Tr) ?? "—";
            StatSize.Text = FormatBytes(st.DatabaseBytes);
        }

        private void RefreshStats_Click(object sender, RoutedEventArgs e) => LoadStats();

        private void ResetStats_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Kopyalama ve yapıştırma sayaçları sıfırlansın mı?\nGeçmişteki kayıtlar silinmez.",
                    "ClipStack", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _db.ResetStats();
            LoadStats();
        }

        private async void Compact_Click(object sender, RoutedEventArgs e)
        {
            long before = _db.GetStats().DatabaseBytes;
            await RunBusyAsync(_db.CompactAsync, "Veritabanı sıkıştırılamadı",
                () => $"Veritabanı sıkıştırıldı.\n{FormatBytes(before)} → {FormatBytes(_db.GetStats().DatabaseBytes)}");
        }

        private async void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Sabitlenmemiş tüm kayıtlar silinsin mi? Bu işlem geri alınamaz.",
                    "ClipStack", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _db.ClearUnpinned();
            await RunBusyAsync(_db.CompactAsync, "Geçmiş temizlendi ama veritabanı sıkıştırılamadı", successMessage: null);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : v.ToString("0.0", Tr) + " " + units[u];
        }

        // ---------- Yedekleme ----------

        private void LoadBackup()
        {
            AutoBackupCheck.IsChecked = Settings.Current.AutoBackup;
            LoadBackupInfo();
        }

        private void LoadBackupInfo()
        {
            string info = $"Son {Settings.AutoBackupKeep} yedek saklanır: {Settings.BackupFolder}";
            try
            {
                var last = Directory.Exists(Settings.BackupFolder)
                    ? new DirectoryInfo(Settings.BackupFolder).GetFiles("history-*.db")
                        .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
                    : null;
                info += last != null
                    ? $"\nSon otomatik yedek: {last.LastWriteTime.ToString("dd.MM.yyyy HH:mm", Tr)} ({FormatBytes(last.Length)})"
                    : "\nHenüz otomatik yedek yok.";
            }
            catch { /* klasör okunamıyorsa sadece yolu göster */ }
            AutoBackupInfo.Text = info;
        }

        private async void Backup_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Yedeği kaydet",
                Filter = "ClipStack yedeği (*.db)|*.db",
                FileName = $"ClipStack-yedek-{DateTime.Now:yyyy-MM-dd}.db",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(this) != true) return;

            var choice = MessageBox.Show(this,
                "Yedek şifreli olsun mu?\n\n" +
                "Evet (önerilir): Sadece bu Windows hesabında geri yüklenebilir. Dosyayı ele geçiren okuyamaz.\n\n" +
                "Hayır: Başka bilgisayarda da geri yüklenebilir, ama içindeki her şey (parolalar dahil) " +
                "dosyayı açan herkes tarafından okunabilir.",
                "ClipStack", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (choice == MessageBoxResult.Cancel) return;
            bool encrypted = choice == MessageBoxResult.Yes;

            string file = dlg.FileName;
            await RunBusyAsync(() => Task.Run(() => _db.BackupTo(file, encrypted)), "Yedek alınamadı",
                () => $"{(encrypted ? "Şifreli" : "Şifresiz")} yedek alındı:\n{file}\n" +
                      $"({FormatBytes(new FileInfo(file).Length)})");
        }

        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Geri yüklenecek yedeği seç",
                Filter = "ClipStack yedeği (*.db)|*.db|Tüm dosyalar (*.*)|*.*",
                InitialDirectory = Directory.Exists(Settings.BackupFolder)
                    ? Settings.BackupFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(this) != true) return;

            if (MessageBox.Show(this,
                    "Yedekteki kayıtlar mevcut geçmişe eklenecek. Mevcut kayıtlar silinmez.\n\n" +
                    "Not: Toplam kayıt sayısı sınırı aşarsa, bir sonraki kopyalamada en eski sabitlenmemiş kayıtlar silinir. " +
                    "Hepsini korumak istiyorsan önce Genel sekmesinden sınırı artır.\n\nDevam edilsin mi?",
                    "ClipStack", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            int added = 0;
            string file = dlg.FileName;
            await RunBusyAsync(async () =>
                {
                    added = await Task.Run(() => _db.ImportFrom(file));
                    _db.InvalidateThumbnails();
                },
                "Yedek geri yüklenemedi",
                () => added > 0 ? $"{added.ToString("N0", Tr)} kayıt geri yüklendi." : "Yedekteki tüm kayıtlar zaten geçmişte var.");
        }

        private async void ExportText_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Metinleri dışa aktar",
                Filter = "Metin dosyası (*.txt)|*.txt",
                FileName = $"ClipStack-metinler-{DateTime.Now:yyyy-MM-dd}.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(this) != true) return;

            int count = 0;
            string file = dlg.FileName;
            await RunBusyAsync(async () => count = await Task.Run(() => _db.ExportTexts(file)), "Dışa aktarılamadı",
                () => $"{count.ToString("N0", Tr)} metin kaydı dışa aktarıldı:\n{file}");
        }

        /// <summary>
        /// Uzun işlemi arka planda çalıştırır. Bu sırada pencere donmaz (yeniden çizilir, taşınabilir) ama
        /// içerik devre dışı kalır ki aynı işlem iki kez başlatılmasın. Sonucu ya da hatayı mesajla bildirir.
        /// </summary>
        private async Task RunBusyAsync(Func<Task> action, string errorTitle, Func<string>? successMessage)
        {
            var root = (UIElement)Content;
            root.IsEnabled = false;
            Cursor = Cursors.Wait;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log.Error(errorTitle, ex);
                MessageBox.Show(this, $"{errorTitle}:\n{ex.Message}", "ClipStack", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                root.IsEnabled = true;
                Cursor = null;
                if (StatsTab.IsSelected) LoadStats();
            }
            if (successMessage != null)
                MessageBox.Show(this, successMessage(), "ClipStack", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(Settings.BackupFolder);
            OpenFolder(Settings.BackupFolder);
        }

        // ---------- Hakkında ----------

        private void LoadAbout()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            AboutVersion.Text = "Sürüm " + (version?.ToString(3) ?? "?");
            AboutHotkey.Text = Settings.HotkeyText;
            AboutRuntime.Text = $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}";
            AboutDataFolder.Text = Settings.DataFolder;
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Settings.DataFolder);

        private void OpenFolder(string folder)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "ClipStack");
            }
        }
    }
}
