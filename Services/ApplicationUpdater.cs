using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SharedUpdates;

public sealed record SelfUpdateOptions(
    string ApplicationName,
    string PortableFlavor,
    bool PreserveToolkitConfiguration,
    bool IsEnglish);

public static class ApplicationUpdater
{
    private const long MaxAssetBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public static async Task<GitHubReleaseAsset> PrepareAndLaunchAsync(
        UpdateCheckResult update,
        SelfUpdateOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(options);
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            throw new InvalidOperationException(Message(options, "无法确定当前程序路径。", "Unable to locate the running application."));

        var executableName = $"{options.ApplicationName}.exe";
        if (!Path.GetFileName(processPath).Equals(executableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Message(options, "当前运行环境不支持自动更新，请使用发布包运行。", "Self-update is unavailable in this environment. Run the packaged application."));

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X64 => "win-x64",
            _ => throw new PlatformNotSupportedException(Message(options, "当前系统架构暂不支持自动更新。", "Self-update is not supported on this architecture."))
        };
        var installDirectory = GetInstallationDirectory(processPath);
        var standalone = !File.Exists(Path.Combine(installDirectory, $"{options.ApplicationName}.runtimeconfig.json"));
        var asset = SelectAsset(update.ReleaseAssets, options.ApplicationName, architecture,
            standalone ? "standalone" : options.PortableFlavor);
        if (asset is null)
            throw new InvalidOperationException(Message(options,
                $"没有找到适用于 {architecture} 的当前版本安装包。",
                $"No matching {architecture} package was found for this installation."));

        ValidateAsset(asset, update, options);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{options.ApplicationName}-update-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(tempRoot, asset.Name);
        var extractPath = Path.Combine(tempRoot, "extracted");
        Directory.CreateDirectory(tempRoot);
        try
        {
            await DownloadAsync(asset, archivePath, progress, cancellationToken).ConfigureAwait(false);
            var digest = await GetDigestAsync(asset, cancellationToken).ConfigureAwait(false);
            VerifyDigest(asset with { Digest = digest }, archivePath, options);
            ExtractArchiveSafely(archivePath, extractPath, options);

            var stagedExecutables = Directory.EnumerateFiles(extractPath, executableName, SearchOption.AllDirectories).ToArray();
            if (stagedExecutables.Length != 1)
                throw new InvalidDataException(Message(options, "更新包中缺少程序文件。", "The update package does not contain the application executable."));
            var stagedExecutable = stagedExecutables[0];
            using (var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(stagedExecutable)))
            {
                var expectedMachine = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? System.Reflection.PortableExecutable.Machine.Arm64 : System.Reflection.PortableExecutable.Machine.Amd64;
                if (pe.PEHeaders.CoffHeader.Machine != expectedMachine)
                    throw new InvalidDataException(Message(options, "更新包中的程序架构不匹配。", "The executable architecture does not match this installation."));
            }
            var stagedVersion = FileVersionInfo.GetVersionInfo(stagedExecutable).ProductVersion?.Split('+', 2)[0];
            if (!string.Equals(stagedVersion, update.LatestVersion.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Message(options, "更新包的程序版本与检查结果不一致。", "The package version does not match the checked release."));
            var stagedDirectory = Path.GetDirectoryName(stagedExecutable)!;
            if (File.Exists(Path.Combine(stagedDirectory, $"{options.ApplicationName}.runtimeconfig.json")) == standalone)
                throw new InvalidDataException(Message(options, "更新包的发布类型与当前安装不一致。", "The package flavor does not match the installation."));
            var scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                options.ApplicationName, "UpdateLogs");
            File.WriteAllText(scriptPath, BuildUpdaterScript(
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                stagedDirectory,
                processPath,
                tempRoot,
                logDirectory,
                options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (!CanWriteToDirectory(installDirectory))
                throw new UnauthorizedAccessException(Message(options,
                    "当前程序目录不可写，更新未开始。请使用有写入权限的目录运行程序。",
                    "The application directory is not writable. The update has not started. Run from a writable directory."));
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException(Message(options, "无法启动更新程序。", "Unable to start the updater."));
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!File.Exists(Path.Combine(tempRoot, "ready")))
                {
                    if (File.Exists(Path.Combine(tempRoot, "error.txt")))
                        throw new IOException(await File.ReadAllTextAsync(Path.Combine(tempRoot, "error.txt"), cancellationToken).ConfigureAwait(false));
                    if (helper.HasExited || DateTime.UtcNow >= deadline)
                        throw new IOException(Message(options, "更新程序未能完成准备，当前程序将保持运行。", "The updater could not prepare. The current application will keep running."));
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                throw;
            }

            progress?.Report(1);
            return asset;
        }
        catch
        {
            // Keep helper diagnostics if preparation failed; no installed files were changed.
            if (!File.Exists(Path.Combine(tempRoot, "error.txt"))) TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    public static GitHubReleaseAsset? SelectAsset(
        IReadOnlyList<GitHubReleaseAsset> assets,
        string applicationName,
        string architecture,
        string flavor)
    {
        var exactName = $"{applicationName}-{architecture}-{flavor}.zip";
        return assets.FirstOrDefault(asset => asset.Name.Equals(exactName, StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(asset =>
                   asset.Name.StartsWith($"{applicationName}-{architecture}-{flavor}-", StringComparison.OrdinalIgnoreCase) &&
                   asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    internal static string GetInstallationDirectory(string executablePath) =>
        Path.GetDirectoryName(Path.GetFullPath(executablePath))
        ?? throw new InvalidOperationException("The executable has no installation directory.");

    private static void ValidateAsset(GitHubReleaseAsset asset, UpdateCheckResult update, SelfUpdateOptions options)
    {
        var releaseUri = new Uri(update.ReleasePageUrl);
        var releaseSegments = releaseUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var expectedPath = releaseSegments.Length >= 2
            ? $"/{releaseSegments[0]}/{releaseSegments[1]}/releases/download/{Uri.EscapeDataString(update.LatestVersion)}/{Uri.EscapeDataString(asset.Name)}"
            : "";
        if (releaseUri.Scheme != Uri.UriSchemeHttps || !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) ||
            asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException(Message(options, "更新包下载地址无效。", "The update download URL is invalid."));
        if (asset.SizeBytes is < 0 or > MaxAssetBytes)
            throw new InvalidDataException(Message(options, "更新包大小无效。", "The update package size is invalid."));
    }

    private static async Task DownloadAsync(
        GitHubReleaseAsset asset,
        string archivePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("SoftwareToolkit-self-update/1.0");
        using var response = await Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var expectedBytes = response.Content.Headers.ContentLength ?? asset.SizeBytes;
        if (expectedBytes > MaxAssetBytes) throw new InvalidDataException("Invalid update download size.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            received += count;
            if (received > MaxAssetBytes) throw new InvalidDataException("Update download exceeded the allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (expectedBytes > 0)
                progress?.Report(Math.Min(0.9, received / (double)expectedBytes * 0.9));
        }
        if (received <= 0)
            throw new InvalidDataException("The downloaded update package is empty.");
        if (expectedBytes > 0 && received != expectedBytes)
            throw new InvalidDataException("The update download is incomplete.");
        if (asset.SizeBytes > 0 && received != asset.SizeBytes)
            throw new InvalidDataException($"Update download size mismatch: expected {asset.SizeBytes}, received {received}.");
    }

    private static async Task<string> GetDigestAsync(GitHubReleaseAsset asset, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(asset.Digest)) return asset.Digest;
        // API-rate-limit fallback uses the checksum published alongside the exact ZIP.
        using var response = await Client.GetAsync(asset.DownloadUrl + ".sha256",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[257];
        var received = 0;
        while (received < buffer.Length)
        {
            var count = await input.ReadAsync(buffer.AsMemory(received), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            received += count;
        }
        if (received == buffer.Length) throw new InvalidDataException("Invalid release checksum file.");
        return "sha256:" + Encoding.UTF8.GetString(buffer, 0, received).Trim();
    }

    internal static void VerifyDigest(GitHubReleaseAsset asset, string archivePath, SelfUpdateOptions options)
    {
        if (string.IsNullOrWhiteSpace(asset.Digest)) throw new InvalidDataException("The release has no verifiable SHA-256 checksum.");
        var separator = asset.Digest.IndexOf(':');
        if (separator <= 0 || !asset.Digest[..separator].Equals("sha256", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported release checksum algorithm.");
        var expected = asset.Digest[(separator + 1)..].Trim();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid release checksum.");
        using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Message(options, "更新包校验失败，文件可能不完整。", "Update package verification failed."));
    }

    internal static void ExtractArchiveSafely(string archivePath, string extractPath, SelfUpdateOptions options)
    {
        Directory.CreateDirectory(extractPath);
        var extractRoot = Path.GetFullPath(extractPath) + Path.DirectorySeparatorChar;
        long expandedBytes = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("The update package contains too many entries.");
        foreach (var entry in archive.Entries)
        {
            // Existing Windows release ZIPs use backslashes; validate after normalizing
            // both separator styles so traversal checks also cover those packages.
            var entryPath = entry.FullName.Replace('\\', '/');
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                entryPath.Contains(':') ||
                entryPath.Split('/').Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
                throw new InvalidDataException("The update package contains an unsafe entry.");
            expandedBytes += entry.Length;
            if (expandedBytes > 1024L * 1024 * 1024)
                throw new InvalidDataException(Message(options, "更新包解压后过大。", "The extracted update package is too large."));
            var destination = Path.GetFullPath(Path.Combine(extractPath, entryPath));
            if (!destination.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Message(options, "更新包包含不安全的路径。", "The update package contains an unsafe path."));
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static bool CanWriteToDirectory(string directory)
    {
        var probe = Path.Combine(directory, $".update-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            return false;
        }
    }

    internal static string BuildUpdaterScript(
        int processId,
        long processStartTicks,
        string stagedDirectory,
        string executablePath,
        string tempRoot,
        string logDirectory,
        SelfUpdateOptions options)
    {
        static string Q(string value) => value.Replace("'", "''");
        using var source = typeof(ApplicationUpdater).Assembly.GetManifestResourceStream("SharedUpdates.ApplyUpdate.ps1")
            ?? throw new InvalidOperationException("The embedded updater is missing.");
        using var reader = new StreamReader(source, Encoding.UTF8);
        var script = reader.ReadToEnd();
        return $$"""
            & {
            {{script}}
            } -ApplicationProcessId {{processId}} -ApplicationStartTicks {{processStartTicks}} -ExecutablePath '{{Q(executablePath)}}' -StagedDirectory '{{Q(stagedDirectory)}}' -Workspace '{{Q(tempRoot)}}' -LogDirectory '{{Q(logDirectory)}}' -PreserveToolkitFiles:${{options.PreserveToolkitConfiguration.ToString().ToLowerInvariant()}} -English:${{options.IsEnglish.ToString().ToLowerInvariant()}}
            """;
    }

    private static string Message(SelfUpdateOptions options, string chinese, string english) =>
        options.IsEnglish ? english : chinese;

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
