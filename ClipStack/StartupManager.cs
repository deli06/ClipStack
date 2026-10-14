using System;
using System.IO;
using Microsoft.Win32;

namespace ClipStack
{
    /// <summary>
    /// "Windows ile başlat" ayarı. Yönetici izni gerektirmeyen HKCU\...\Run anahtarını kullanır.
    ///
    /// Windows 10/11'de Görev Yöneticisi > Başlangıç uygulamaları ekranında bir uygulama devre dışı
    /// bırakılırsa Run kaydı silinmez; StartupApproved\Run altına "devre dışı" işareti yazılır.
    /// Bu yüzden durumu okurken ve yazarken iki anahtara birden bakıyoruz; aksi halde kutu işaretli
    /// görünür ama uygulama açılışta başlamaz.
    /// </summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "ClipStack";

        /// <summary>Çalışan exe'nin tam yolu (dotnet run ile çalışırken bin klasöründeki ClipStack.exe).</summary>
        public static string ExePath => Environment.ProcessPath ?? "";

        private static string Command => $"\"{ExePath}\" --startup";

        public static bool IsEnabled()
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string)
                return false;

            // StartupApproved değeri: ilk bayt çift (02, 06) = açık, tek (03, 07) = Görev Yöneticisi'nden kapatılmış.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1)
                return false;

            return true;
        }

        public static void SetEnabled(bool enable)
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
            {
                if (enable)
                    run.SetValue(ValueName, Command, RegistryValueKind.String);
                else
                    run.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            // Görev Yöneticisi'nin "devre dışı" işaretini temizle; değer yoksa Windows açık kabul eder.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        /// <summary>
        /// Uygulama başka klasöre taşındıysa (kayıttaki exe artık yoksa) yolu güncel tutar.
        /// Kayıttaki exe hâlâ duruyorsa dokunmaz: aksi halde geliştirirken bir kez <c>dotnet run</c>
        /// yapmak, kalıcı kurulumun başlangıç kaydını bin\Debug içindeki exe'ye çevirirdi.
        /// </summary>
        public static void RefreshPathIfEnabled()
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (run?.GetValue(ValueName) is string current &&
                    ShouldRefresh(current, Command, File.Exists))
                {
                    run.SetValue(ValueName, Command, RegistryValueKind.String);
                }
            }
            catch (Exception ex)
            {
                // Kayıt defterine erişim engelliyse (grup ilkesi vb.) uygulama çalışmaya devam eder; ama iz kalsın:
                // kullanıcı "Windows ile başlatmıyor" derse sebebi log'da görünür.
                Log.Error("\"Windows ile başlat\" kaydındaki yol güncellenemedi", ex);
            }
        }

        /// <summary>Kayıttaki komut güncellenmeli mi? Sadece komut farklıysa ve içindeki exe artık yoksa.</summary>
        internal static bool ShouldRefresh(string registered, string wanted, Func<string, bool> fileExists)
        {
            if (string.Equals(registered, wanted, StringComparison.OrdinalIgnoreCase))
                return false;
            string exe = ExtractExePath(registered);
            return exe.Length == 0 || !fileExists(exe);
        }

        /// <summary><c>"C:\yol\ClipStack.exe" --startup</c> biçimindeki komuttan exe yolunu çıkarır.</summary>
        internal static string ExtractExePath(string command)
        {
            command = command.Trim();
            if (command.StartsWith('"'))
            {
                int end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : "";
            }
            int space = command.IndexOf(' ');
            return space < 0 ? command : command.Substring(0, space);
        }
    }
}
