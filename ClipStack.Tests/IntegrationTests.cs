using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Xunit;
using Forms = System.Windows.Forms;

namespace ClipStack.Tests
{
    /// <summary>
    /// Çalışan gerçek ClipStack'e karşı uçtan uca testler. Gerçek panoyu ve gerçek geçmişi kullanır,
    /// bu yüzden sadece CLIPSTACK_IT=1 ortam değişkeni varken çalışır. Eklediği kayıtları siler.
    /// </summary>
    public sealed class IntegrationFactAttribute : FactAttribute
    {
        public IntegrationFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CLIPSTACK_IT") != "1")
                Skip = "Uçtan uca test: CLIPSTACK_IT=1 ile ve ClipStack çalışırken çalıştırın.";
        }
    }

    [Collection("Settings")]
    public class IntegrationTests
    {
        private static readonly string ExePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\ClipStack\bin\Debug\net8.0-windows\ClipStack.exe"));

        private static string RealDb => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipStack", "history.db");

        /// <summary>Çalışan ClipStack'in anahtarı (aynı Windows hesabında çalıştığımız için DPAPI çözer).</summary>
        private static byte[] RealKey => DatabaseKey.LoadOrCreate(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipStack", "db.key"));

        [IntegrationFact]
        public void CopiedTextAndFiles_ReachTheHistory_AndFilePasteFormatIsCorrect()
        {
            Assert.NotNull(RunningInstance());
            string marker = "CLIPSTACK-IT-" + Guid.NewGuid().ToString("N");
            string tempFile = Path.Combine(Path.GetTempPath(), marker + ".txt");
            File.WriteAllText(tempFile, "test");
            try
            {
                RunSta(() => Forms.Clipboard.SetText("metin " + marker));
                Assert.Equal("text", WaitForKind(marker));

                // Uygulamanın kendi "dosya yapıştır" kodu (bu süreçten çağrılınca ClipStack onu yeni kopya sayar).
                RunSta(() => Assert.True(PasteService.SetClipboardFiles(new[] { tempFile })));
                Assert.Equal("files", WaitForKind(marker + ".txt", exactPath: tempFile));

                RunSta(() =>
                {
                    var drop = Forms.Clipboard.GetFileDropList();
                    Assert.Equal(tempFile, drop.Cast<string>().Single());
                    Assert.Equal(tempFile, Forms.Clipboard.GetText());
                    var effect = (MemoryStream)Forms.Clipboard.GetData("Preferred DropEffect")!;
                    Assert.Equal(1, BitConverter.ToInt32(effect.ToArray(), 0)); // kopyala, taşı değil
                });
            }
            finally
            {
                File.Delete(tempFile);
                Exec("DELETE FROM clips WHERE text LIKE $m;", "%" + marker + "%");
            }
        }

        [IntegrationFact]
        public void RealHistory_IsEncryptedOnDisk()
        {
            Assert.NotNull(RunningInstance());
            Assert.False(ClipDatabase.IsPlaintextDatabase(RealDb));

            // Ana dosya da WAL da şifreli olmalı (SQLCipher WAL sayfalarını da şifreler).
            string marker = "CLIPSTACK-IT-" + Guid.NewGuid().ToString("N");
            try
            {
                RunSta(() => Forms.Clipboard.SetText("wal kontrol " + marker));
                Assert.Equal("text", WaitForKind(marker));
                foreach (string file in new[] { RealDb, RealDb + "-wal" })
                {
                    if (!File.Exists(file)) continue;
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    string raw = Encoding.UTF8.GetString(ms.ToArray());
                    Assert.DoesNotContain("CREATE TABLE", raw);
                    Assert.DoesNotContain(marker, raw);
                }
            }
            finally
            {
                Exec("DELETE FROM clips WHERE text LIKE $m;", "%" + marker + "%");
            }
            Assert.Throws<DatabaseKeyException>(() => ClipDatabase.OpenConnection(RealDb, key: null, readOnly: true).Dispose());
        }

        [IntegrationFact]
        public void CopiedToken_IsNotSaved_ButNormalTextIs()
        {
            Assert.NotNull(RunningInstance());
            string marker = "CLIPSTACK-IT-" + Guid.NewGuid().ToString("N");
            // Gerçek olmayan ama GitHub token biçimine uyan değer.
            string token = "ghp_" + marker.Replace("-", "").PadRight(36, 'x').Substring(0, 36);
            try
            {
                RunSta(() => Forms.Clipboard.SetText("token: " + token + " " + marker + "-secret"));
                Thread.Sleep(1000);
                RunSta(() => Forms.Clipboard.SetText("normal " + marker));
                Assert.Equal("text", WaitForKind("normal " + marker));
                Assert.Null(WaitForKind(marker + "-secret", timeoutMs: 300));
            }
            finally
            {
                Exec("DELETE FROM clips WHERE text LIKE $m;", "%" + marker + "%");
            }
        }

        [IntegrationFact]
        public void PausedMonitor_IgnoresCopies_UntilResumed()
        {
            string marker = "CLIPSTACK-IT-" + Guid.NewGuid().ToString("N");
            var seen = new System.Collections.Generic.List<string>();
            try
            {
                RunSta(() =>
                {
                    using var monitor = new ClipboardMonitor { Paused = true };
                    monitor.TextCopied += (text, _, _, _) => seen.Add(text);

                    Forms.Clipboard.SetText("duraklatılmış " + marker);
                    Pump(800);
                    monitor.Paused = false;
                    Forms.Clipboard.SetText("sürdürülmüş " + marker);
                    Pump(800);
                });
                Assert.DoesNotContain(seen, t => t.Contains("duraklatılmış"));
                Assert.Contains(seen, t => t.Contains("sürdürülmüş " + marker));
            }
            finally
            {
                // Çalışan ClipStack (duraklatılmamış) bunları da kaydetti; temizle.
                if (RunningInstance() != null)
                    Exec("DELETE FROM clips WHERE text LIKE $m;", "%" + marker + "%");
            }
        }

        [IntegrationFact]
        public void SingleCopy_IsCountedOnce_WithCorrectSourceApp()
        {
            Assert.NotNull(RunningInstance());
            string marker = "CLIPSTACK-IT-" + Guid.NewGuid().ToString("N");
            long Copies()
            {
                using var conn = ClipDatabase.OpenConnection(RealDb, RealKey, readOnly: true);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT IFNULL((SELECT value FROM stats WHERE key = 'copies'), 0);";
                return (long)cmd.ExecuteScalar()!;
            }
            try
            {
                long before = Copies();
                // WinForms SetText (OLE, kalıcı kopya) tek kopyalamada iki pano bildirimi üretir.
                RunSta(() => Forms.Clipboard.SetText("tek kopya " + marker));
                Assert.Equal("text", WaitForKind(marker));
                Thread.Sleep(500); // olası ikinci işlemenin de bitmesine fırsat ver
                Assert.Equal(before + 1, Copies());

                using var conn = ClipDatabase.OpenConnection(RealDb, RealKey, readOnly: true);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT source_app FROM clips WHERE text LIKE $m;";
                cmd.Parameters.AddWithValue("$m", "%" + marker + "%");
                Assert.Equal(Process.GetCurrentProcess().ProcessName, cmd.ExecuteScalar() as string);
            }
            finally
            {
                Exec("DELETE FROM clips WHERE text LIKE $m;", "%" + marker + "%");
            }
        }

        [IntegrationFact]
        public void RunningApp_IsPerMonitorDpiAware()
        {
            var running = RunningInstance()!;
            Assert.Equal(0, GetProcessDpiAwareness(running.Handle, out int awareness));
            Assert.Equal(2, awareness); // PROCESS_PER_MONITOR_DPI_AWARE
        }

        [IntegrationFact]
        public void CopyingLargeImage_DoesNotFreezeTheUi()
        {
            var running = RunningInstance()!;
            IntPtr uiWindow = FindWindow(running.Id, visibleOnly: false);
            Assert.NotEqual(IntPtr.Zero, uiWindow);

            byte[] png = ImageProcessingTests.MakePng(3500, 2500, noise: true);
            Assert.True(png.Length < Settings.MaxImageBytes, $"test görseli {png.Length / 1024 / 1024} MB");
            string hash = "img:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png));

            long worstMs = 0;
            using var stop = new ManualResetEventSlim();
            var pinger = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    var sw = Stopwatch.StartNew();
                    SendMessageTimeout(uiWindow, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0x0002 /* SMTO_ABORTIFHUNG */, 5000, out _);
                    worstMs = Math.Max(worstMs, sw.ElapsedMilliseconds);
                    Thread.Sleep(20);
                }
            });
            try
            {
                pinger.Start();
                RunSta(() => Assert.True(PasteService.SetClipboardImage(png)));
                Assert.True(WaitUntil(() => CountByHash(hash) == 1, timeoutMs: 15000), "görsel geçmişe eklenmedi");
            }
            finally
            {
                stop.Set();
                pinger.Join();
                Exec("DELETE FROM clips WHERE hash = $m;", hash);
            }
            Assert.True(worstMs < 250, $"arayüz {worstMs} ms yanıt vermedi");
        }

        [IntegrationFact]
        public void PopupSize_IsRememberedAfterResize()
        {
            var running = RunningInstance()!;
            string settingsPath = Path.Combine(Path.GetDirectoryName(RealDb)!, "settings.json");

            PressHotkey();
            Assert.True(WaitUntil(() => IsPopupVisible(running.Id)));
            IntPtr popup = FindWindow(running.Id, visibleOnly: true);
            GetWindowRect(popup, out var original);
            uint dpi = GetDpiForWindow(popup);
            int newW = original.Right - original.Left + 120, newH = original.Bottom - original.Top + 80;
            try
            {
                SetWindowPos(popup, IntPtr.Zero, 0, 0, newW, newH, 0x0002 | 0x0004 | 0x0010); // NOMOVE|NOZORDER|NOACTIVATE
                Thread.Sleep(300);
                PressHotkey(); // kapat → boyut kaydedilir
                Assert.True(WaitUntil(() => !IsPopupVisible(running.Id)));

                double expectedH = Math.Round(newH * 96.0 / dpi);
                Assert.True(WaitUntil(() => Math.Abs(ReadSetting(settingsPath, "PopupHeight") - expectedH) <= 2),
                    $"PopupHeight {ReadSetting(settingsPath, "PopupHeight")} ≠ {expectedH}");

                // Yeniden açınca kaydedilen boyutla açılmalı.
                PressHotkey();
                Assert.True(WaitUntil(() => IsPopupVisible(running.Id)));
                GetWindowRect(FindWindow(running.Id, visibleOnly: true), out var reopened);
                Assert.True(Math.Abs(reopened.Bottom - reopened.Top - newH) <= 3, $"yükseklik {reopened.Bottom - reopened.Top} ≠ {newH}");
            }
            finally
            {
                // Kullanıcının boyutunu geri koy.
                if (!IsPopupVisible(running.Id)) { PressHotkey(); WaitUntil(() => IsPopupVisible(running.Id)); }
                SetWindowPos(FindWindow(running.Id, visibleOnly: true), IntPtr.Zero, 0, 0,
                    original.Right - original.Left, original.Bottom - original.Top, 0x0002 | 0x0004 | 0x0010);
                Thread.Sleep(300);
                PressHotkey();
                WaitUntil(() => !IsPopupVisible(running.Id));
            }
        }

        private static double ReadSetting(string path, string name)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = System.Text.Json.JsonDocument.Parse(fs);
            return doc.RootElement.GetProperty(name).GetDouble();
        }

        private static long CountByHash(string hash)
        {
            using var conn = ClipDatabase.OpenConnection(RealDb, RealKey, readOnly: true);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM clips WHERE hash = $h;";
            cmd.Parameters.AddWithValue("$h", hash);
            return (long)cmd.ExecuteScalar()!;
        }

        /// <summary>ClipStack sürecinin "ClipStack" başlıklı penceresi (geçmiş penceresi); gizli olabilir.</summary>
        private static IntPtr FindWindow(int pid, bool visibleOnly)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint p);
                if (p != pid || (visibleOnly && !IsWindowVisible(hwnd))) return true;
                var sb = new StringBuilder(64);
                GetWindowText(hwnd, sb, sb.Capacity);
                if (sb.ToString() != "ClipStack") return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(IntPtr process, out int value);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

        /// <summary>Bu iş parçacığının mesaj kuyruğunu verilen süre boyunca işler (pano bildirimleri gelsin diye).</summary>
        private static void Pump(int ms)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        [IntegrationFact]
        public void SecondInstance_OpensPopupOfRunningInstance_AndExits()
        {
            var running = RunningInstance()!;
            Assert.False(IsPopupVisible(running.Id));

            using var second = Process.Start(ExePath)!;
            Assert.True(second.WaitForExit(10_000), "ikinci örnek kapanmadı");
            Assert.True(WaitUntil(() => IsPopupVisible(running.Id)), "geçmiş penceresi açılmadı");

            PressHotkey(); // açıkken kısayol: kapatır
            Assert.True(WaitUntil(() => !IsPopupVisible(running.Id)), "kısayol pencereyi kapatmadı");
        }

        [IntegrationFact]
        public void SecondInstance_WithStartupArg_ExitsSilently()
        {
            var running = RunningInstance()!;
            using var second = Process.Start(ExePath, App.StartupArg)!;
            Assert.True(second.WaitForExit(10_000));
            Thread.Sleep(700);
            Assert.False(IsPopupVisible(running.Id));
        }

        [IntegrationFact]
        public void Hotkey_OpensThenClosesPopup()
        {
            var running = RunningInstance()!;
            PressHotkey();
            Assert.True(WaitUntil(() => IsPopupVisible(running.Id)), "kısayol pencereyi açmadı");
            PressHotkey();
            Assert.True(WaitUntil(() => !IsPopupVisible(running.Id)), "kısayol pencereyi kapatmadı");
        }

        // ---------- yardımcılar ----------

        private static Process? RunningInstance() =>
            Process.GetProcessesByName("ClipStack").FirstOrDefault();

        private static string? WaitForKind(string marker, string? exactPath = null, int timeoutMs = 5000)
        {
            string? kind = null;
            WaitUntil(timeoutMs: timeoutMs, condition: () =>
            {
                using var conn = ClipDatabase.OpenConnection(RealDb, RealKey, readOnly: true);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = exactPath != null
                    ? "SELECT kind FROM clips WHERE text = $m;"
                    : "SELECT kind FROM clips WHERE text LIKE $m;";
                cmd.Parameters.AddWithValue("$m", exactPath ?? "%" + marker + "%");
                kind = cmd.ExecuteScalar() as string;
                return kind != null;
            });
            return kind;
        }

        private static void Exec(string sql, string param)
        {
            using var conn = ClipDatabase.OpenConnection(RealDb, RealKey);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$m", param);
            cmd.ExecuteNonQuery();
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(100);
            }
            return condition();
        }

        private static void RunSta(Action action)
        {
            Exception? error = null;
            var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw new Exception("STA iş parçacığında hata", error);
        }

        private static void PressHotkey()
        {
            var s = new AppSettings(); // varsayılan kısayol: Ctrl+Shift+V
            Assert.Equal(Native.MOD_CONTROL | Native.MOD_SHIFT, s.HotkeyModifiers);
            Native.INPUT K(ushort vk, bool up) => new()
            {
                type = Native.INPUT_KEYBOARD,
                U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } }
            };
            var inputs = new[]
            {
                K(Native.VK_CONTROL, false), K(Native.VK_SHIFT, false), K(Native.VK_V, false),
                K(Native.VK_V, true), K(Native.VK_SHIFT, true), K(Native.VK_CONTROL, true),
            };
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        }

        private static bool IsPopupVisible(int pid)
        {
            bool found = false;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint p);
                if (p == pid && IsWindowVisible(hwnd))
                {
                    var sb = new StringBuilder(64);
                    GetWindowText(hwnd, sb, sb.Capacity);
                    if (sb.ToString() == "ClipStack") { found = true; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    }
}
