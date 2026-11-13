using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AIMaster.Services;

/// <summary>
/// Brings the DeepSeek desktop app to the foreground. The app publishes only one deep link
/// (<c>dsh://open</c>), which launches or focuses it; when it is already running this helper focuses
/// the existing window directly so clicking a conversation does not silently leave it behind.
/// </summary>
internal static class DeepSeekDesktopApp
{
    internal const string ProcessName = "DeepSeek Harness";
    internal const string OpenUri = "dsh://open";
    private const uint SwRestore = 9;

    /// <summary>Finds the app's main window without changing focus.</summary>
    internal static bool TryGetWindow(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id <= 0) continue;
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        handle = process.MainWindowHandle;
                        return true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }

            // Electron renderers can own the visible window, so fall back to the process id.
            var ids = processes.Select(process => process.Id).ToHashSet();
            var resolved = IntPtr.Zero;
            EnumWindows((candidate, _) =>
            {
                if (!IsWindowVisible(candidate) || GetWindowTextLength(candidate) == 0) return true;
                GetWindowThreadProcessId(candidate, out var owner);
                if (!ids.Contains((int)owner)) return true;
                resolved = candidate;
                return false;
            }, IntPtr.Zero);
            handle = resolved;
            return handle != IntPtr.Zero;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    /// <summary>Restores and focuses the running app; returns false when it is not running.</summary>
    internal static bool TryFocus()
    {
        if (!TryGetWindow(out var handle)) return false;
        try
        {
            if (IsIconic(handle)) ShowWindow(handle, SwRestore);
            if (SetForegroundWindow(handle)) return true;
            // Windows only grants foreground rights to the active thread; attach to it briefly.
            var foreground = GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
            var targetThread = GetWindowThreadProcessId(handle, out _);
            if (foregroundThread == 0 || targetThread == 0) return false;
            if (!AttachThreadInput(foregroundThread, targetThread, true)) return false;
            try
            {
                BringWindowToTop(handle);
                return SetForegroundWindow(handle);
            }
            finally
            {
                AttachThreadInput(foregroundThread, targetThread, false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Focuses the running app, or launches it through the only deep link it exposes.</summary>
    internal static bool Activate(string? openUri)
    {
        if (TryFocus()) return true;
        if (string.IsNullOrWhiteSpace(openUri)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(openUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);
}
