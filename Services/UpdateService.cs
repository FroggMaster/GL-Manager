using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Services;

public partial class UpdateService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/FroggMaster/GL-Manager/releases/latest";
    private static readonly string[] RcSeparator = ["-rc"];

    private static readonly HttpClient Client;

    static UpdateService()
    {
        Client = new HttpClient();
        Client.DefaultRequestHeaders.Add("User-Agent", "GreenLuma-Manager");
    }

    private static string CurrentVersion => MainWindow.Version;

    [GeneratedRegex("\"tag_name\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex TagNameRegex();

    [GeneratedRegex("\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex BrowserDownloadUrlRegex();

    [GeneratedRegex("\"body\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex BodyRegex();

    public static async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            Logger.Info("Checking for updates from GitHub");
            var response = await Client.GetStringAsync(GitHubApiUrl);
            return ParseUpdateInfo(response);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for updates");
            return null;
        }
    }

    private static UpdateInfo? ParseUpdateInfo(string jsonResponse)
    {
        var tagMatch = TagNameRegex().Match(jsonResponse);
        var downloadMatch = BrowserDownloadUrlRegex().Match(jsonResponse);
        var bodyMatch = BodyRegex().Match(jsonResponse);

        if (!tagMatch.Success)
            return null;

        var latestTag = tagMatch.Groups[1].Value;
        var downloadUrl = downloadMatch.Success ? downloadMatch.Groups[1].Value : string.Empty;
        var releaseNotes = bodyMatch.Success
            ? bodyMatch.Groups[1].Value.Replace("\\n", "\n").Replace("\\r", "\r")
            : string.Empty;

        var currentNormalized = NormalizeVersion(CurrentVersion);
        var latestNormalized = NormalizeVersion(latestTag);

        Logger.Debug($"Update check: current={CurrentVersion} ({currentNormalized}), latest={latestTag} ({latestNormalized})");

        return new UpdateInfo
        {
            CurrentVersion = CurrentVersion,
            LatestVersion = FormatDisplayVersion(latestTag),
            LatestVersionTag = latestTag,
            UpdateAvailable =
                string.Compare(latestNormalized, currentNormalized, StringComparison.OrdinalIgnoreCase) > 0,
            DownloadUrl = downloadUrl,
            ReleaseNotes = releaseNotes
        };
    }

    private static string NormalizeVersion(string version)
    {
        var cleaned = version.ToLower().Trim().Replace("v", "");

        if (string.IsNullOrEmpty(cleaned))
            return "00000.00000.00000.0000.0000";

        if (cleaned.StartsWith("rc") && !cleaned.Contains(".0.0"))
        {
            var rcPart = cleaned[2..];
            var rcParts = rcPart.Split('.');

            var rcMajor = ParseVersionPart(rcParts, 0);
            var rcMinor = rcParts.Length > 1 ? ParseVersionPart(rcParts, 1) : 0;

            return $"00001.00000.00000.{rcMajor:D4}.{rcMinor:D4}";
        }

        var parts = cleaned.Split('-', 2);
        var baseVersion = parts[0];

        var versionParts = baseVersion.Split('.');
        var major = ParseVersionPart(versionParts, 0);
        var minor = ParseVersionPart(versionParts, 1);
        var patch = ParseVersionPart(versionParts, 2);

        if (parts.Length == 1) return $"{major:D5}.{minor:D5}.{patch:D5}.9999.9999";

        var prerelease = parts[1];

        if (prerelease.StartsWith("rc"))
        {
            var rcPart = prerelease[2..];
            var rcParts = rcPart.Split('.');

            var rcMajor = ParseVersionPart(rcParts, 0);
            var rcMinor = rcParts.Length > 1 ? ParseVersionPart(rcParts, 1) : 0;

            return $"{major:D5}.{minor:D5}.{patch:D5}.{rcMajor:D4}.{rcMinor:D4}";
        }

        return $"{major:D5}.{minor:D5}.{patch:D5}.0000.0000";
    }

    private static int ParseVersionPart(string[] parts, int index)
    {
        if (index >= parts.Length)
            return 0;

        if (int.TryParse(parts[index], out var result))
            return result;

        return 0;
    }

    private static string FormatDisplayVersion(string version)
    {
        var cleaned = version.ToLower().Replace("v", "");

        if (cleaned.Contains("-rc"))
        {
            var parts = cleaned.Split(RcSeparator, StringSplitOptions.None);
            if (parts.Length > 1) return "RC" + parts[1].ToUpper();
        }

        if (cleaned.StartsWith("rc")) return cleaned.ToUpper();

        return cleaned;
    }

    public static async Task<bool> PerformAutoUpdateAsync(string downloadUrl)
    {
        string? stagingDir = null;

        try
        {
            Logger.Info($"Performing auto-update from: {downloadUrl}");

            var currentExePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
            {
                Logger.Error("Auto-update failed: could not resolve the running executable path");
                return false;
            }

            stagingDir = Path.Combine(Path.GetTempPath(), $"GLM_Update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);

            var tempExePath = await DownloadUpdate(downloadUrl, stagingDir);

            CreateAndExecuteUpdateScript(stagingDir, tempExePath, currentExePath);

            Logger.Info("Update script started; shutting down to apply the update");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Auto-update failed");
            TryDeleteDirectory(stagingDir);
            return false;
        }
    }

    private static async Task<string> DownloadUpdate(string downloadUrl, string stagingDir)
    {
        var tempExePath = Path.Combine(stagingDir, "GreenLuma-Manager.exe");

        using var response = await Client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using (var fileStream = new FileStream(
                         tempExePath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         useAsync: true))
        {
            await response.Content.CopyToAsync(fileStream);
        }

        // Never let an error page or a truncated download replace the app.
        var downloaded = new FileInfo(tempExePath);
        if (downloaded.Length < 1024)
            throw new InvalidDataException($"Downloaded update is too small ({downloaded.Length} bytes).");

        using (var headerStream = File.OpenRead(tempExePath))
        {
            if (headerStream.ReadByte() != 'M' || headerStream.ReadByte() != 'Z')
                throw new InvalidDataException("Downloaded update is not a valid Windows executable.");
        }

        Logger.Info($"Update downloaded to '{tempExePath}' ({downloaded.Length} bytes)");
        return tempExePath;
    }

    private static void CreateAndExecuteUpdateScript(string stagingDir, string tempExePath, string currentExePath)
    {
        var scriptPath = Path.Combine(stagingDir, "update.ps1");
        var scriptContent = GenerateUpdateScript(stagingDir, scriptPath, tempExePath, currentExePath);

        // Windows PowerShell 5.1 needs a BOM to read non-ASCII paths correctly.
        File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath()
        });
    }

    private static string GenerateUpdateScript(
        string stagingDir,
        string scriptPath,
        string tempExePath,
        string currentExePath)
    {
        var currentExeDir = Path.GetDirectoryName(currentExePath) ?? string.Empty;
        var backupPath = currentExePath + ".old";

        return $$"""
            $ErrorActionPreference = 'Stop'

            $processId = {{Environment.ProcessId}}
            $source = '{{EscapePowerShellLiteral(tempExePath)}}'
            $target = '{{EscapePowerShellLiteral(currentExePath)}}'
            $backup = '{{EscapePowerShellLiteral(backupPath)}}'
            $workingDirectory = '{{EscapePowerShellLiteral(currentExeDir)}}'
            $scriptPath = '{{EscapePowerShellLiteral(scriptPath)}}'
            $stagingDirectory = '{{EscapePowerShellLiteral(stagingDir)}}'

            function Wait-ForApplicationExit {
                param([int]$Id, [int]$TimeoutSeconds = 30)

                $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
                while ($true) {
                    if ($null -eq (Get-Process -Id $Id -ErrorAction SilentlyContinue)) {
                        return $true
                    }
                    if ((Get-Date) -gt $deadline) {
                        return $false
                    }
                    Start-Sleep -Milliseconds 200
                }
            }

            function Move-WithRetry {
                param([string]$Source, [string]$Destination, [int]$TimeoutSeconds = 10)

                $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
                while ($true) {
                    try {
                        Move-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
                        return
                    }
                    catch {
                        if ((Get-Date) -gt $deadline) {
                            throw
                        }
                        Start-Sleep -Milliseconds 250
                    }
                }
            }

            $replaced = $false
            try {
                if (-not (Wait-ForApplicationExit -Id $processId)) {
                    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Milliseconds 500
                }

                Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
                Move-WithRetry -Source $target -Destination $backup

                try {
                    Move-WithRetry -Source $source -Destination $target
                    $replaced = $true
                }
                catch {
                    Move-WithRetry -Source $backup -Destination $target
                    throw
                }
            }
            finally {
                if (Test-Path -LiteralPath $target) {
                    Start-Process -FilePath $target -WorkingDirectory $workingDirectory
                }

                if ($replaced) {
                    Start-Sleep -Milliseconds 750
                    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
                }

                Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }
            """;
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not remove update staging directory '{path}': {ex.Message}");
        }
    }
}