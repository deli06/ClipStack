using System;
using System.Collections.Generic;
using System.Text;

namespace ClipStack
{
    /// <summary>
    /// Öne gelen son "gerçek" pencereyi hatırlar.
    ///
    /// Neden: Popup tepsi ikonundan açıldığında o anki ön pencere görev çubuğudur; yapıştırma oraya
    /// gitmesin diye, kullanıcının en son çalıştığı uygulama penceresi burada tutulur.
    /// Kendi pencerelerimiz ve görev çubuğu/tepsi pencereleri sayılmaz.
    /// </summary>
    public sealed class ForegroundTracker : IDisposable
    {
        /// <summary>Görev çubuğu, tepsi ve taşma (overflow) alanının pencere sınıfları.</summary>
        private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland",
        };

        private readonly uint _ownPid = (uint)Environment.ProcessId;
        private readonly Native.WinEventDelegate _callback; // GC toplamasın diye alanda tutulur
        private readonly IntPtr _hook;

        /// <summary>En son öne gelen uygun pencere; hiç yoksa IntPtr.Zero.</summary>
        public IntPtr LastWindow { get; private set; }

        public ForegroundTracker()
        {
            _callback = OnForegroundChanged;
            // OUTOFCONTEXT: geri çağrı bizim iş parçacığımızın mesaj döngüsünden gelir.
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            if (_hook == IntPtr.Zero)
                Log.Error("Ön pencere izleyicisi kurulamadı; tepsiden yapıştırma hedefi bulunamayabilir.");

            IntPtr current = Native.GetForegroundWindow();
            if (IsEligible(current))
                LastWindow = current;
        }

        /// <summary>
        /// Yapıştırma hedefini döndürür: verilen pencere uygunsa o, değilse en son uygun pencere.
        /// </summary>
        public IntPtr ResolveTarget(IntPtr candidate) => IsEligible(candidate) ? candidate : LastWindow;

        private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint thread, uint time)
        {
            if (IsEligible(hwnd))
                LastWindow = hwnd;
        }

        private bool IsEligible(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
                return false;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == _ownPid)
                return false;
            return !IsShellClass(GetClassName(hwnd));
        }

        internal static bool IsShellClass(string className) => ShellClasses.Contains(className);

        private static string GetClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            return Native.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
                Native.UnhookWinEvent(_hook);
        }
    }
}
