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
        var standalone = new FileInfo(processPath).Length >= 8L * 1024 * 1024;
        var asset = SelectAsset(update.ReleaseAssets, options.ApplicationName, architecture,
            standalone ? "standalone" : options.PortableFlavor);
        if (asset is null)
            throw new InvalidOperationException(Message(options,
                $"没有找到适用于 {architecture} 的当前版本安装包。",
                $"No matching {architecture} package was found for this installation."));

        ValidateAsset(asset, options);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{options.ApplicationName}-update-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(tempRoot, asset.Name);
        var extractPath = Path.Combine(tempRoot, "extracted");
        Directory.CreateDirectory(tempRoot);
        try
        {
            await DownloadAsync(asset, archivePath, progress, cancellationToken).ConfigureAwait(false);
            VerifyDigest(asset, archivePath, options);
            ExtractArchiveSafely(archivePath, extractPath, options);

            var stagedExecutable = Directory.EnumerateFiles(extractPath, executableName, SearchOption.AllDirectories)
                .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
                .FirstOrDefault();
            if (stagedExecutable is null)
                throw new InvalidDataException(Message(options, "更新包中缺少程序文件。", "The update package does not contain the application executable."));

            var stagedDirectory = Path.GetDirectoryName(stagedExecutable)!;
            var installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var scriptPath = Path.Combine(Path.GetTempPath(), $"{options.ApplicationName}-apply-update-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, BuildUpdaterScript(
                Environment.ProcessId,
                stagedDirectory,
                installDirectory,
                executableName,
                tempRoot,
                options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var requiresElevation = !CanWriteToDirectory(installDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (requiresElevation) startInfo.Verb = "runas";
            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException(Message(options, "无法启动更新程序。", "Unable to start the updater."));

            progress?.Report(1);
            return asset;
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
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

    private static void ValidateAsset(GitHubReleaseAsset asset, SelfUpdateOptions options)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
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
        if (asset.SizeBytes > 0 && received != asset.SizeBytes)
            throw new InvalidDataException($"Update download size mismatch: expected {asset.SizeBytes}, received {received}.");
    }

    private static void VerifyDigest(GitHubReleaseAsset asset, string archivePath, SelfUpdateOptions options)
    {
        if (string.IsNullOrWhiteSpace(asset.Digest)) return;
        var separator = asset.Digest.IndexOf(':');
        if (separator <= 0 || !asset.Digest[..separator].Equals("sha256", StringComparison.OrdinalIgnoreCase)) return;
        var expected = asset.Digest[(separator + 1)..].Trim();
        using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Message(options, "更新包校验失败，文件可能不完整。", "Update package verification failed."));
    }

    private static void ExtractArchiveSafely(string archivePath, string extractPath, SelfUpdateOptions options)
    {
        Directory.CreateDirectory(extractPath);
        var extractRoot = Path.GetFullPath(extractPath) + Path.DirectorySeparatorChar;
        long expandedBytes = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            expandedBytes += entry.Length;
            if (expandedBytes > 1024L * 1024 * 1024)
                throw new InvalidDataException(Message(options, "更新包解压后过大。", "The extracted update package is too large."));
            var destination = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));
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

    private static string BuildUpdaterScript(
        int processId,
        string stagedDirectory,
        string installDirectory,
        string executableName,
        string tempRoot,
        SelfUpdateOptions options)
    {
        static string Q(string value) => value.Replace("'", "''");
        var errorTitle = options.IsEnglish ? "Update failed" : "更新失败";
        var errorPrefix = options.IsEnglish
            ? "The update could not be installed. The previous version has been restored.\n\n"
            : "无法安装更新，已恢复原版本。\n\n";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $processId = {{processId}}
            $stagedDir = '{{Q(stagedDirectory)}}'
            $installDir = '{{Q(installDirectory.TrimEnd(Path.DirectorySeparatorChar))}}'
            $executableName = '{{Q(executableName)}}'
            $tempRoot = '{{Q(tempRoot)}}'
            $preserveToolkitFiles = ${{options.PreserveToolkitConfiguration.ToString().ToLowerInvariant()}}
            $backupDir = Join-Path $tempRoot 'backup'
            $changed = @()

            try {
                $deadline = [DateTime]::UtcNow.AddSeconds(60)
                while (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
                    if ([DateTime]::UtcNow -ge $deadline) { throw 'The application did not exit in time.' }
                    Start-Sleep -Milliseconds 250
                }

                foreach ($file in Get-ChildItem -LiteralPath $stagedDir -File -Recurse -Force) {
                    $relative = $file.FullName.Substring($stagedDir.Length).TrimStart('\')
                    if ($preserveToolkitFiles -and ($relative -ieq 'tools.json' -or $relative -like 'tools\*')) { continue }
                    $destination = Join-Path $installDir $relative
                    $backup = Join-Path $backupDir $relative
                    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
                    $existed = Test-Path -LiteralPath $destination -PathType Leaf
                    if ($existed) {
                        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force | Out-Null
                        Copy-Item -LiteralPath $destination -Destination $backup -Force
                    }
                    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
                    $changed += [pscustomobject]@{ Path = $destination; Backup = $backup; Existed = $existed }
                }

                Start-Process -FilePath (Join-Path $installDir $executableName)
                Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            }
            catch {
                for ($index = $changed.Count - 1; $index -ge 0; $index--) {
                    $item = $changed[$index]
                    if ($item.Existed -and (Test-Path -LiteralPath $item.Backup)) {
                        Copy-Item -LiteralPath $item.Backup -Destination $item.Path -Force -ErrorAction SilentlyContinue
                    } elseif (-not $item.Existed) {
                        Remove-Item -LiteralPath $item.Path -Force -ErrorAction SilentlyContinue
                    }
                }
                Start-Process -FilePath (Join-Path $installDir $executableName) -ErrorAction SilentlyContinue
                Add-Type -AssemblyName PresentationFramework
                [System.Windows.MessageBox]::Show('{{Q(errorPrefix)}}' + $_.Exception.Message, '{{Q(errorTitle)}}') | Out-Null
                Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            }
            """;
    }

    private static string Message(SelfUpdateOptions options, string chinese, string english) =>
        options.IsEnglish ? english : chinese;

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
