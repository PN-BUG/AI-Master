using System;
using System.IO;
using System.Runtime.InteropServices;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyTaskbarIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster taskbar 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic explorer = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try
        {
            var executable = Path.Combine(directory, "AIMaster.exe");
            File.WriteAllText(executable, "shortcut fixture; never executed");
            var pinned = Path.Combine(directory, "My pinned app.lnk");
            var unrelated = Path.Combine(directory, "AIMaster unrelated.lnk");
            foreach (var path in new[] { pinned, unrelated })
            {
                dynamic link = shell.CreateShortcut(path);
                try
                {
                    link.TargetPath = path == pinned ? executable : Path.Combine(directory, "Other.exe");
                    link.Arguments = "--keep-my-arguments";
                    link.WorkingDirectory = directory;
                    link.Description = "Keep my description";
                    link.IconLocation = executable + ",0";
                    link.Save();
                }
                finally { Marshal.ReleaseComObject(link); }
            }
            File.WriteAllText(Path.Combine(directory, "broken.lnk"), "invalid shortcut");
            var lockedPin = Path.Combine(directory, "Locked pin.lnk");
            File.Copy(pinned, lockedPin);
            var unrelatedBytes = File.ReadAllBytes(unrelated);

            using (var locked = new FileStream(lockedPin, FileMode.Open, FileAccess.Read, FileShare.Read))
                NotificationShortcut.SynchronizeTaskbarShortcuts(directory, executable, "AIMaster.Test.Identity");

            dynamic folder = explorer.NameSpace(directory);
            dynamic item = folder.ParseName(Path.GetFileName(pinned));
            try
            {
                Check((string)item.ExtendedProperty("System.AppUserModel.ID") == "AIMaster.Test.Identity",
                    "a legacy pin gets the running application's explicit identity");
            }
            finally { Marshal.ReleaseComObject(item); Marshal.ReleaseComObject(folder); }
            dynamic repaired = shell.CreateShortcut(pinned);
            try
            {
                Check((string)repaired.TargetPath == executable &&
                      (string)repaired.Arguments == "--keep-my-arguments" &&
                      (string)repaired.WorkingDirectory == directory &&
                      (string)repaired.Description == "Keep my description" &&
                      ((string)repaired.IconLocation).StartsWith(executable, StringComparison.OrdinalIgnoreCase),
                    "migration preserves the user's shortcut target, arguments, directory, description and icon");
            }
            finally { Marshal.ReleaseComObject(repaired); }
            Check(Convert.ToHexString(unrelatedBytes) == Convert.ToHexString(File.ReadAllBytes(unrelated)),
                "a similarly named shortcut targeting another executable stays byte-for-byte unchanged");
            var modified = File.GetLastWriteTimeUtc(pinned);
            NotificationShortcut.SynchronizeTaskbarShortcuts(directory, executable.ToUpperInvariant(), "AIMaster.Test.Identity");
            Check(File.GetLastWriteTimeUtc(pinned) == modified,
                "matching identities are not rewritten on subsequent startups");
            NotificationShortcut.SynchronizeTaskbarShortcuts(directory, executable.ToUpperInvariant(), "AIMaster.Test.ChangedIdentity");
            folder = explorer.NameSpace(directory);
            item = folder.ParseName(Path.GetFileName(pinned));
            try
            {
                Check((string)item.ExtendedProperty("System.AppUserModel.ID") == "AIMaster.Test.ChangedIdentity",
                    "a stale explicit identity is updated with case-insensitive executable matching");
            }
            finally { Marshal.ReleaseComObject(item); Marshal.ReleaseComObject(folder); }
            NotificationShortcut.SynchronizeTaskbarShortcuts(Path.Combine(directory, "missing"), executable, "AIMaster.Test.Identity");
            Check(!Directory.Exists(Path.Combine(directory, "missing")), "startup never creates pins for an unpinned app");
            Console.WriteLine("Taskbar identity checks passed.");
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
            Marshal.ReleaseComObject(explorer);
            Directory.Delete(directory, recursive: true);
        }
    }
}
