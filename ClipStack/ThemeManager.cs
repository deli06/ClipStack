using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ClipStack
{
    /// <summary>
    /// Açık/koyu tema. Renkler Themes\Dark.xaml ve Themes\Light.xaml'dedir; uygulama kaynaklarındaki ilk
    /// sözlük değiştirilir, DynamicResource kullanan tüm pencereler anında güncellenir. "Sistem" seçiliyken
    /// Windows'un uygulama teması (Ayarlar → Kişiselleştirme → Renkler) izlenir.
    /// </summary>
    public static class ThemeManager
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private static bool _listening;

        public static bool IsDark { get; private set; } = true;

        public static void Initialize()
        {
            Apply(Settings.Current.Theme);
            if (!_listening)
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _listening = true;
            }
        }

        public static void Shutdown()
        {
            if (_listening)
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listening = false;
        }

        /// <summary>Temayı uygular: renk sözlüğünü değiştirir ve açık pencerelerin başlık çubuğunu günceller.</summary>
        public static void Apply(AppTheme theme)
        {
            IsDark = ResolveDark(theme, SystemPrefersDark());
            var app = Application.Current;
            if (app == null) return;

            var colors = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/ClipStack;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml")
            };
            var merged = app.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = colors;
            else merged.Add(colors);

            foreach (Window w in app.Windows)
                ApplyTitleBar(w);
        }

        internal static bool ResolveDark(AppTheme theme, bool systemPrefersDark) => theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => systemPrefersDark
        };

        /// <summary>Windows uygulamalar için koyu modu mu kullanıyor (AppsUseLightTheme = 0).</summary>
        internal static bool SystemPrefersDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pencerenin başlık çubuğunu temaya uydurur (Windows 10 20H1+ / 11). Pencere tutamacı yoksa
        /// oluşunca uygulanır.
        /// </summary>
        public static void ApplyTitleBar(Window window)
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero)
            {
                window.SourceInitialized -= OnSourceInitialized;
                window.SourceInitialized += OnSourceInitialized;
                return;
            }
            int dark = IsDark ? 1 : 0;
            // 20: DWMWA_USE_IMMERSIVE_DARK_MODE; eski Windows 10 sürümlerinde 19.
            if (DwmSetWindowAttribute(helper.Handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(helper.Handle, 19, ref dark, sizeof(int));
        }

        private static void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (sender is Window w)
            {
                w.SourceInitialized -= OnSourceInitialized;
                ApplyTitleBar(w);
            }
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General || Settings.Current.Theme != AppTheme.System)
                return;
            // Olay başka bir iş parçacığından gelebilir.
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (ResolveDark(AppTheme.System, SystemPrefersDark()) != IsDark)
                    Apply(AppTheme.System);
            });
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
