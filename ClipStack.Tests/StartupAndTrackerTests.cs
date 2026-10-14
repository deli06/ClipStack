using System;
using Xunit;

namespace ClipStack.Tests
{
    public class StartupManagerTests
    {
        private const string Installed = @"C:\Araçlar\ClipStack\ClipStack.exe";
        private const string DevBuild = @"C:\denemeler\Pano\ClipStack\bin\Debug\net8.0-windows\ClipStack.exe";

        [Theory]
        [InlineData("\"C:\\Program Files\\ClipStack\\ClipStack.exe\" --startup", @"C:\Program Files\ClipStack\ClipStack.exe")]
        [InlineData("C:\\a\\ClipStack.exe --startup", @"C:\a\ClipStack.exe")]
        [InlineData("C:\\a\\ClipStack.exe", @"C:\a\ClipStack.exe")]
        [InlineData("  \"C:\\a b\\x.exe\"", @"C:\a b\x.exe")]
        [InlineData("\"", "")]
        public void ExtractExePath_ParsesQuotedAndBareCommands(string command, string expected) =>
            Assert.Equal(expected, StartupManager.ExtractExePath(command));

        [Fact]
        public void DevBuildRun_DoesNotOverwriteExistingInstalledPath()
        {
            bool refresh = StartupManager.ShouldRefresh(
                $"\"{Installed}\" --startup", $"\"{DevBuild}\" --startup", path => path == Installed);
            Assert.False(refresh);
        }

        [Fact]
        public void MovedApp_UpdatesPath_WhenRegisteredExeIsGone()
        {
            bool refresh = StartupManager.ShouldRefresh(
                $"\"{Installed}\" --startup", $"\"{DevBuild}\" --startup", _ => false);
            Assert.True(refresh);
        }

        [Fact]
        public void SameCommand_NeverRefreshes()
        {
            string cmd = $"\"{Installed}\" --startup";
            Assert.False(StartupManager.ShouldRefresh(cmd, cmd.ToUpperInvariant(), _ => false));
        }
    }

    public class ForegroundTrackerTests
    {
        [Theory]
        [InlineData("Shell_TrayWnd", true)]
        [InlineData("shell_traywnd", true)]
        [InlineData("Shell_SecondaryTrayWnd", true)]
        [InlineData("NotifyIconOverflowWindow", true)]
        [InlineData("TopLevelWindowForOverflowXamlIsland", true)]
        [InlineData("Notepad", false)]
        [InlineData("Chrome_WidgetWin_1", false)]
        [InlineData("", false)]
        public void TaskbarAndTrayWindows_AreNotPasteTargets(string className, bool isShell) =>
            Assert.Equal(isShell, ForegroundTracker.IsShellClass(className));

        [Fact]
        public void ResolveTarget_FallsBackToLastWindow_ForInvalidCandidate()
        {
            using var tracker = new ForegroundTracker();
            Assert.Equal(tracker.LastWindow, tracker.ResolveTarget(IntPtr.Zero));
            Assert.Equal(tracker.LastWindow, tracker.ResolveTarget(new IntPtr(0x7FFFFFF0))); // geçersiz tutamaç
        }
    }
}
