using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace AIMaster.Services;

/// <summary>Keeps the unpackaged toast, running process and shortcuts on the same Shell identity.</summary>
internal static class NotificationShortcut
{
    public static void EnsureRegistered()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing application executable path.");
        var (appId, activator) = FindRegistration(executable);
        // Set this before any UI is created, even if shortcut registration later fails.
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appId));
        SynchronizeTaskbarShortcuts(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), executable, appId);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AIMaster");
        Directory.CreateDirectory(directory);
        var shortcut = Path.Combine(directory, "AIMaster.lnk");
        var temporary = Path.Combine(directory, "AIMaster.pending.lnk");
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
        try
        {
            link.SetPath(executable);
            link.SetArguments("");
            link.SetDescription("AIMaster");
            link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetIconLocation(executable, 0);
            var properties = (IPropertyStore)link;
            var appIdKey = new PropertyKey(5);
            var appIdValue = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            var activatorKey = new PropertyKey(26);
            var activatorValue = new PropVariant { Type = 72, Pointer = Marshal.AllocCoTaskMem(16) };
            try
            {
                Marshal.StructureToPtr(activator, activatorValue.Pointer, false);
                properties.SetValue(ref appIdKey, ref appIdValue);
                properties.SetValue(ref activatorKey, ref activatorValue);
                properties.Commit();
                ((IPersistFile)link).Save(temporary, true);
            }
            finally
            {
                Marshal.FreeCoTaskMem(appIdValue.Pointer);
                Marshal.FreeCoTaskMem(activatorValue.Pointer);
            }
            File.Move(temporary, shortcut, overwrite: true);
            SHChangeNotify(0x2000, 5, shortcut, IntPtr.Zero); // SHCNE_UPDATEITEM / SHCNF_PATHW
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    internal static void SynchronizeTaskbarShortcuts(string directory, string executable, string appId)
    {
        // Repair existing pins only. Do not create pins or touch another installation's shortcuts.
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var shortcut in Directory.EnumerateFiles(directory, "*.lnk"))
            {
                IShellLinkW? link = null;
                try
                {
                    link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(
                        new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
                    var file = (IPersistFile)link;
                    file.Load(shortcut, 2); // STGM_READWRITE; never resolve or launch the target.
                    var target = new StringBuilder(32768);
                    link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
                    var targetPath = Environment.ExpandEnvironmentVariables(target.ToString());
                    if (string.IsNullOrEmpty(targetPath) ||
                        !string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(executable),
                            StringComparison.OrdinalIgnoreCase)) continue;

                    var properties = (IPropertyStore)link;
                    var key = new PropertyKey(5);
                    properties.GetValue(ref key, out var current);
                    try
                    {
                        if (current.Type == 31 && Marshal.PtrToStringUni(current.Pointer) == appId) continue;
                    }
                    finally { PropVariantClear(ref current); }

                    var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(appId) };
                    try
                    {
                        properties.SetValue(ref key, ref value);
                        properties.Commit();
                        file.Save(shortcut, true);
                    }
                    finally { Marshal.FreeCoTaskMem(value.Pointer); }
                    SHChangeNotify(0x2000, 5, shortcut, IntPtr.Zero);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
                {
                    // A locked/corrupt pin must not disable notifications or prevent the next pin being repaired.
                    System.Diagnostics.Debug.WriteLine($"[TaskbarIdentity] {shortcut}: {ex.Message}");
                }
                finally { if (link is not null) Marshal.ReleaseComObject(link); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[TaskbarIdentity] {ex.Message}");
        }
    }

    private static (string AppId, Guid Activator) FindRegistration(string executable)
    {
        using var applications = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
        if (applications is not null)
            foreach (var appId in applications.GetSubKeyNames())
            {
                using var application = applications.OpenSubKey(appId);
                if (application?.GetValue("CustomActivator") is not string value || !Guid.TryParse(value, out var activator)) continue;
                using var server = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{activator}}}\LocalServer32");
                if (string.Equals(server?.GetValue(null) as string, $"\"{executable}\" -ToastActivated", StringComparison.OrdinalIgnoreCase))
                    return (appId, activator);
            }
        throw new InvalidOperationException("AIMaster's Windows notification identity was not registered.");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item, IntPtr other);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(uint id)
    {
        public Guid Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        public uint Id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr data, uint flags);
        void GetIDList(out IntPtr item);
        void SetIDList(IntPtr item);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
