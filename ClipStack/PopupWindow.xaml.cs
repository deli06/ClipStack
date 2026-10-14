using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipStack.Resources;

namespace ClipStack
{
    public partial class PopupWindow : Window
    {
        /// <summary>Önizleme açıkken listeye göre genişlik oranı (liste 1, önizleme 0.75).</summary>
        private const double PreviewRatio = 0.75;

        /// <summary>Önizlemede gösterilecek en fazla metin; daha uzunu kesilir (TextBox çok uzun metinde yavaşlar).</summary>
        private const int PreviewMaxChars = 50_000;
        private const int PreviewMaxFiles = 200;

        /// <summary>Geri alınabilecek en fazla silme (görseller büyük olabildiği için sınırlı).</summary>
        private const int MaxUndo = 10;

        private readonly ClipDatabase _db;
        private IntPtr _target;
        private bool _busy;

        /// <summary>Hızlı yazarken her tuşta veritabanını taramamak için kısa bekleme.</summary>
        private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

        /// <summary>Oklarla hızlı gezinirken her öğe için tam metni/görseli okumamak için kısa bekleme.</summary>
        private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };

        private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };

        private readonly List<DeletedClip> _undo = new();
        private long _lastDeleteTick;
        private long _lastTypeTick;

        public PopupWindow(ClipDatabase db)
        {
            InitializeComponent();
            _db = db;
            _searchTimer.Tick += (_, _) => RunSearch();
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); UpdatePreview(); };
            _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); ShowPersistentStatus(); };

            // Pencere tutamacını baştan oluştur; böylece gösterilmeden önce DPI dönüşümü yapılabilir.
            new WindowInteropHelper(this).EnsureHandle();

            Deactivated += (_, _) => { if (!_busy) Hide(); };
            IsVisibleChanged += (_, _) => { if (!IsVisible) RememberSizeAndPosition(); };
            PreviewKeyDown += OnPreviewKeyDown;
            ApplyPreviewLayout();
        }

        /// <summary>
        /// Pencereyi açar. Zaten açıksa: <paramref name="toggle"/> true ise (kısayola tekrar basıldı) kapatır,
        /// değilse sadece öne getirir. Açıkken hedef pencere değiştirilmez.
        /// </summary>
        public void ShowAt(IntPtr targetWindow, bool toggle = false)
        {
            if (IsVisible)
            {
                if (toggle) Hide();
                else Activate();
                return;
            }

            _target = targetWindow;
            SearchBox.Text = "";
            _searchTimer.Stop(); // metni temizlemek aramayı tetikledi; Refresh zaten yapıyor
            _statusTimer.Stop();
            ShowPersistentStatus();
            Refresh();
            ApplyPreviewLayout(); // boyutu ayarlardan al (başka yerden değişmiş olabilir)
            Place();

            Show();
            // Başka DPI'lı monitöre geçtiyse WPF pencereyi yeniden boyutlandırmış olabilir; gerçek boyutla sığdır.
            WindowPlacement.KeepOnScreen(this);
            Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }

        /// <summary>Geçmiş değişti (yeni kopya geldi). Pencere açıksa listeyi seçimi koruyarak yeniler.</summary>
        public void OnHistoryChanged()
        {
            if (IsVisible && !_busy)
                Refresh();
        }

        // ---------- Liste ----------

        /// <summary>Listeyi yeniler; seçili öğe hâlâ listedeyse seçili kalır.</summary>
        private void Refresh()
        {
            long? selectedId = (List.SelectedItem as ClipItem)?.Id;
            var items = LoadItems();
            List.ItemsSource = items;

            var keep = selectedId.HasValue ? items.FirstOrDefault(i => i.Id == selectedId) : null;
            List.SelectedItem = keep ?? items.FirstOrDefault();
            if (List.SelectedItem != null)
                List.ScrollIntoView(List.SelectedItem);
        }

        private List<ClipItem> LoadItems()
        {
            var items = _db.Search(SearchBox.Text);
            for (int i = 0; i < items.Count && i < 9; i++)
                items[i].QuickKey = (i + 1).ToString();
            return items;
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Pencere gizliyken yapılan değişiklik (ShowAt'ta temizleme) kullanıcının yazması sayılmaz.
            if (IsVisible)
                _lastTypeTick = Environment.TickCount64;
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void RunSearch()
        {
            _searchTimer.Stop();
            List.ItemsSource = LoadItems();
            List.SelectedIndex = List.Items.Count > 0 ? 0 : -1;
        }

        /// <summary>
        /// Bekleyen bir arama varsa hemen çalıştırır. Kullanıcı yazıp beklemeden Enter'a basarsa
        /// eski listedeki öğe yapıştırılmasın diye, listeye dayanan her tuştan önce çağrılır.
        /// </summary>
        private void FlushPendingSearch()
        {
            if (_searchTimer.IsEnabled)
                RunSearch();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                FlushPendingSearch();

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            int quick = QuickIndex(e.Key);
            if (ctrl && quick >= 0)
            {
                // Ctrl+1…9: listedeki N. öğeyi yapıştır (Shift ile düz metin).
                if (quick < List.Items.Count && List.Items[quick] is ClipItem q)
                    Choose(q, paste: true, plainText: shift);
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    Hide();
                    e.Handled = true;
                    break;

                case Key.Down:
                    Move(+1);
                    e.Handled = true;
                    break;

                case Key.Up:
                    Move(-1);
                    e.Handled = true;
                    break;

                case Key.PageDown:
                    Move(+10);
                    e.Handled = true;
                    break;

                case Key.PageUp:
                    Move(-10);
                    e.Handled = true;
                    break;

                case Key.Enter:
                    // Enter: biçimiyle yapıştır · Shift+Enter: düz metin · Ctrl+Enter: sadece kopyala
                    if (List.SelectedItem is ClipItem item)
                        Choose(item, paste: !ctrl, plainText: shift);
                    e.Handled = true;
                    break;

                case Key.F3:
                    TogglePreview();
                    e.Handled = true;
                    break;

                case Key.P when ctrl:
                    if (List.SelectedItem is ClipItem toPin)
                    {
                        _db.TogglePin(toPin.Id);
                        Refresh();
                    }
                    e.Handled = true;
                    break;

                case Key.O when ctrl:
                    Hide();
                    ((App)Application.Current).ShowOptions();
                    e.Handled = true;
                    break;

                case Key.Delete when shift:
                    if (List.SelectedItem is ClipItem toDelete)
                        DeleteWithUndo(toDelete);
                    e.Handled = true;
                    break;

                // Arama kutusunda yazdıktan sonra Ctrl+Z metni geri alır; son işlem silme ise kaydı geri getirir.
                case Key.Z when ctrl && _undo.Count > 0 && _lastDeleteTick > _lastTypeTick:
                    UndoDelete();
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>1…9 tuşları (üst sıra veya sayısal tuş takımı) için 0…8, diğerleri için -1.</summary>
        private static int QuickIndex(Key key) => key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1
        };

        private void Move(int delta)
        {
            if (List.Items.Count == 0) return;
            int i = Math.Clamp(List.SelectedIndex + delta, 0, List.Items.Count - 1);
            List.SelectedIndex = i;
            List.ScrollIntoView(List.SelectedItem);
        }

        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (List.SelectedItem is ClipItem item)
                Choose(item, paste: true, plainText: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        }

        // ---------- Silme / geri alma ----------

        private void DeleteWithUndo(ClipItem item)
        {
            int idx = List.SelectedIndex;
            var deleted = _db.Delete(item.Id);
            List.ItemsSource = LoadItems();
            List.SelectedIndex = Math.Min(idx, List.Items.Count - 1);

            if (deleted == null) return;
            _undo.Add(deleted);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _lastDeleteTick = Environment.TickCount64;
            ShowStatus(Strings.Popup_Deleted);
        }

        private void UndoDelete()
        {
            var last = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            bool restored = _db.Restore(last);

            List.ItemsSource = LoadItems();
            var item = List.Items.OfType<ClipItem>().FirstOrDefault(i => i.Id == last.Id);
            if (item != null)
            {
                List.SelectedItem = item;
                List.ScrollIntoView(item);
            }
            ShowStatus(restored
                ? Strings.Popup_Restored + (_undo.Count > 0 ? Localization.F(Strings.Popup_MoreUndo, _undo.Count) : "")
                : Strings.Popup_RestoreDuplicate);
            _lastDeleteTick = Environment.TickCount64; // art arda Ctrl+Z ile birden çok silme geri alınabilsin
        }

        /// <summary>Kayıt duraklatıldıysa açıklamasını döndürür (App atar); kayıt açıksa null.</summary>
        public Func<string?>? PauseInfo { get; init; }

        /// <summary>Kalıcı durum satırı: kayıt duraklatıldıysa onu gösterir, değilse satırı gizler.</summary>
        private void ShowPersistentStatus()
        {
            string? pause = PauseInfo?.Invoke();
            StatusText.Text = pause ?? "";
            StatusText.Visibility = pause != null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Birkaç saniyelik geçici mesaj; süresi dolunca kalıcı duruma döner.</summary>
        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        // ---------- Yapıştırma ----------

        private async void Choose(ClipItem item, bool paste, bool plainText = false)
        {
            _busy = true;
            try
            {
                Hide();

                bool ok;
                if (item.IsImage)
                {
                    byte[]? png = _db.GetImage(item.Id);
                    ok = png != null && PasteService.SetClipboardImage(png);
                }
                else if (item.IsFiles)
                {
                    string[] files = _db.GetFiles(item.Id);
                    if (files.Length == 0)
                        return; // bu arada silinmiş
                    // Düz metin istenirse dosyalar değil yolları yapıştırılır.
                    ok = plainText
                        ? PasteService.SetClipboardText(string.Join(Environment.NewLine, files))
                        : PasteService.SetClipboardFiles(files);
                }
                else
                {
                    // Listede sadece metnin başı var; tam metni şimdi oku.
                    string? text = _db.GetText(item.Id);
                    if (text == null)
                        return; // bu arada silinmiş (ör. süre dolumu)

                    if (plainText || !item.HasFormat)
                    {
                        ok = PasteService.SetClipboardText(text);
                    }
                    else
                    {
                        var (html, rtf) = _db.GetFormats(item.Id);
                        ok = PasteService.SetClipboardText(text, html, rtf);
                    }
                }

                if (!ok)
                {
                    string? holder = PasteService.ClipboardHolder();
                    Log.Error($"Panoya yazılamadı (id={item.Id}, tür={item.Kind}): pano kilitli" +
                              (holder != null ? $", kilitleyen: {holder}" : ", kilitleyen bilinmiyor"));
                    MessageBox.Show(holder != null
                            ? Localization.F(Strings.Popup_ClipboardLockedBy, holder)
                            : Strings.Popup_ClipboardLocked,
                        "ClipStack");
                    return;
                }
                _db.Touch(item.Id);
                if (paste)
                {
                    // "Sadece kopyala" (Ctrl+Enter) yapıştırma sayılmaz.
                    _db.RecordPaste();
                    await PasteService.PasteIntoAsync(_target);
                }
            }
            catch (Exception ex)
            {
                // async void: burada yakalanmayan hata doğrudan uygulamayı düşürmeye gider.
                Log.Error($"Kayıt yapıştırılamadı (id={item.Id})", ex);
                MessageBox.Show(Localization.F(Strings.Popup_PasteFailed, ex.Message), "ClipStack",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _busy = false;
            }
        }

        // ---------- Önizleme ----------

        private void TogglePreview()
        {
            // Şu anki modun (önizlemeli/önizlemesiz) boyutunu sakla; diğer modun kendi boyutu var.
            if (IsVisible) CaptureSize();
            Settings.Current.ShowPreview = !Settings.Current.ShowPreview;
            SaveSettings();

            ApplyPreviewLayout();
            if (IsVisible)
            {
                UpdateLayout();
                WindowPlacement.KeepOnScreen(this);
            }
        }

        /// <summary>Önizleme sütununu ve pencere boyutunu, kayıtlı ayarlara göre uygular.</summary>
        private void ApplyPreviewLayout()
        {
            var s = Settings.Current;
            bool show = s.ShowPreview;
            PreviewColumn.Width = show ? new GridLength(PreviewRatio, GridUnitType.Star) : new GridLength(0);
            PreviewPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Width = show ? s.PopupWidthWithPreview : s.PopupWidth;
            Height = s.PopupHeight;
            if (show) UpdatePreview();
            else ClearPreview();
        }

        private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!Settings.Current.ShowPreview) return;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void ClearPreview()
        {
            PreviewText.Text = "";
            PreviewImage.Source = null;
        }

        private void UpdatePreview()
        {
            if (!Settings.Current.ShowPreview) return;
            ClearPreview();
            if (List.SelectedItem is not ClipItem item)
            {
                ShowPreviewText("");
                return;
            }

            try
            {
                if (item.IsImage)
                {
                    byte[]? png = _db.GetImage(item.Id);
                    if (png == null) return;
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.StreamSource = new MemoryStream(png);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    // Çok büyük görselleri tam çözünürlükte açıp belleği şişirme.
                    if (item.Width > 1600) bi.DecodePixelWidth = 1600;
                    bi.EndInit();
                    bi.Freeze();
                    PreviewImage.Source = bi;
                    PreviewImage.Visibility = Visibility.Visible;
                    PreviewText.Visibility = Visibility.Collapsed;
                }
                else if (item.IsFiles)
                {
                    var files = _db.GetFiles(item.Id);
                    var sb = new StringBuilder();
                    sb.AppendLine(Localization.F(Strings.Preview_FilesHeader, files.Length)).AppendLine();
                    foreach (var f in files.Take(PreviewMaxFiles))
                    {
                        bool exists = File.Exists(f) || Directory.Exists(f);
                        sb.AppendLine(exists ? f : f + Strings.Preview_FileMissing);
                    }
                    if (files.Length > PreviewMaxFiles)
                        sb.AppendLine(Localization.F(Strings.Preview_MoreFiles, files.Length - PreviewMaxFiles));
                    ShowPreviewText(sb.ToString());
                }
                else
                {
                    string text = _db.GetText(item.Id) ?? "";
                    if (text.Length > PreviewMaxChars)
                        text = text.Substring(0, PreviewMaxChars) + Localization.F(Strings.Preview_Truncated,
                            PreviewMaxChars.ToString("N0", Localization.FormatCulture), text.Length.ToString("N0", Localization.FormatCulture));
                    ShowPreviewText(text);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Önizleme gösterilemedi (id={item.Id})", ex);
                ShowPreviewText(Localization.F(Strings.Preview_Failed, ex.Message));
            }
        }

        private void ShowPreviewText(string text)
        {
            PreviewText.Text = text;
            PreviewText.ScrollToHome();
            PreviewText.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Collapsed;
        }

        // ---------- Konum ----------

        /// <summary>
        /// Pencereyi ayara göre konumlandırır (imlecin yanı / son konum / ekran ortası). Hesap, pencerenin
        /// açılacağı monitörün fiziksel pikselleri ve DPI'ı ile yapılır; böylece farklı ölçekli monitörlerde kaymaz.
        /// </summary>
        private void Place()
        {
            var s = Settings.Current;
            Native.GetCursorPos(out var cursor);

            if (s.PopupPlacement == PopupPlacement.Remember && s.PopupLeftPx is int left && s.PopupTopPx is int top)
            {
                // Kaydedilen nokta artık hiçbir monitörde değilse (monitör çıkarıldı) en yakın monitöre çekilir.
                var area = WindowPlacement.MonitorFromPoint(left, top);
                var (x, y) = WindowPlacement.ClampInto(left, top, area.WorkArea,
                    WindowPlacement.DipToPx(Width, area.Dpi), WindowPlacement.DipToPx(Height, area.Dpi));
                WindowPlacement.MoveTo(this, x, y);
                return;
            }

            var here = WindowPlacement.MonitorFromPoint(cursor.X, cursor.Y);
            int w = WindowPlacement.DipToPx(Width, here.Dpi);
            int h = WindowPlacement.DipToPx(Height, here.Dpi);
            var pos = s.PopupPlacement == PopupPlacement.Cursor
                ? WindowPlacement.NearCursor(cursor.X, cursor.Y, here.WorkArea, w, h, WindowPlacement.DipToPx(8, here.Dpi))
                : WindowPlacement.Center(here.WorkArea, w, h);
            WindowPlacement.MoveTo(this, pos.X, pos.Y);
        }

        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        /// <summary>Kullanıcının boyutlandırdığı pencere boyutunu, şu anki moda (önizlemeli/önizlemesiz) yazar.</summary>
        private void CaptureSize()
        {
            var s = Settings.Current;
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            if (s.ShowPreview) s.PopupWidthWithPreview = Math.Round(ActualWidth);
            else s.PopupWidth = Math.Round(ActualWidth);
            s.PopupHeight = Math.Round(ActualHeight);
        }

        /// <summary>Pencere kapanınca boyutu ve konumu saklar; değiştiyse ayar dosyasına yazar.</summary>
        private void RememberSizeAndPosition()
        {
            var s = Settings.Current;
            var sizeBefore = (s.PopupWidth, s.PopupWidthWithPreview, s.PopupHeight);
            var posBefore = (s.PopupLeftPx, s.PopupTopPx);
            CaptureSize();
            var rect = WindowPlacement.GetWindowRect(this);
            s.PopupLeftPx = rect.Left;
            s.PopupTopPx = rect.Top;

            // "İmlecin yanında" modunda konum her açılışta değişir; dosyayı her kapanışta yazmamak için
            // konum sadece "son konumunda" modunda kalıcı değişiklik sayılır.
            bool sizeChanged = sizeBefore != (s.PopupWidth, s.PopupWidthWithPreview, s.PopupHeight);
            bool posChanged = s.PopupPlacement == PopupPlacement.Remember && posBefore != (s.PopupLeftPx, s.PopupTopPx);
            if (sizeChanged || posChanged)
                SaveSettings();
        }

        private static void SaveSettings()
        {
            try { Settings.Save(); }
            catch (Exception ex) { Log.Error("Pencere ayarları kaydedilemedi", ex); }
        }
    }
}
