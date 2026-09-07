using System.IO;
using System.Text.RegularExpressions;
using GreenLuma_Manager.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GreenLuma_Manager.Services;

public static partial class GreenLumaDeploymentService
{
    private const string ZipPassword = "cs.rin.ru";

    /// <summary>
    /// Extracts the downloaded ZIP and deploys files based on the detected installation type.
    /// When <paramref name="forcedMethod"/> is provided (fresh install), detection is skipped
    /// and the specified method is used directly.
    /// </summary>
    /// <param name="zipPath">Full path to the downloaded .zip file.</param>
    /// <param name="greenLumaPath">GreenLuma installation directory (destination for copied files).</param>
    /// <param name="steamPath">Steam installation directory (destination for user32.dll if applicable).</param>
    /// <param name="forcedMethod">When set, forces a specific install method instead of auto-detecting.</param>
    /// <param name="user32DeployMode">
    /// When <paramref name="forcedMethod"/> is <see cref="GreenLumaInstallMethod.User32"/>,
    /// specifies whether to deploy the user32 DLL directly or via the SF variant.
    /// Defaults to <see cref="User32DeployMode.User32SF"/> when omitted.
    /// </param>
    /// <param name="progressCallback">Optional callback to report status messages.</param>
    /// <param name="numericProgress">Optional callback for numeric progress (0.0 to 1.0).</param>
    public static async Task<GreenLumaDeploymentResult> DeployAsync(
        string zipPath,
        string greenLumaPath,
        string steamPath,
        GreenLumaInstallMethod? forcedMethod = null,
        User32DeployMode? user32DeployMode = null,
        Action<string>? progressCallback = null,
        IProgress<double>? numericProgress = null)
    {
        var result = new GreenLumaDeploymentResult();
        var tmpDir = Path.GetDirectoryName(zipPath)!;
        var extractDir = Path.Combine(tmpDir, "extracted");

        void Report(string msg)
        {
            progressCallback?.Invoke(msg);
            var pct = msg switch
            {
                "Extracting ZIP..." => 0.10,
                "Detecting installation type..." => 0.30,
                "Updating GreenLuma files..." => 0.60,
                "Deploying user32.dll..." => 0.60,
                "Cleaning up..." => 0.90,
                "Done." => 1.0,
                _ => 0.0
            };
            numericProgress?.Report(pct);
        }

        try
        {
            // ── Step 1: Extract ZIP (SharpCompress handles LZMA + password) ──
            Report("Extracting ZIP...");
            if (!File.Exists(zipPath))
            {
                result.ErrorMessage = $"ZIP file not found: {zipPath}";
                return result;
            }

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);

            Logger.Debug($" Extracting: {zipPath}");

            if (!TryExtractZip(zipPath, extractDir, out var extractError))
            {
                result.ErrorMessage = extractError;
                return result;
            }

            // ── Step 2: Find source directory ─────────────────────
            var sourceDir = FindSourceDirectory(extractDir);
            Logger.Debug($" Source dir: {sourceDir}");

            // Log all extracted files
            var allExtractedFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            Logger.Debug($" Total files extracted: {allExtractedFiles.Length}");
            foreach (var f in allExtractedFiles)
                Logger.Debug($"  {Path.GetRelativePath(sourceDir, f)}");

            // Collect files recursively for deployment matching.
            // The ZIP has files spread across root (AppListManager.exe),
            // NormalMode/ (DLLInjector.exe, GreenLuma_2026_x64.dll, etc.)
            // and StealthMode/ (user32SF.dll).
            // When duplicate filenames exist across directories (e.g.
            // x86DedicatedServers/DLLInjector.exe vs NormalMode/DLLInjector.exe),
            // prefer the NormalMode/ copy.
            var allZipFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            var availableFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in allZipFiles)
            {
                var fileName = Path.GetFileName(filePath);
                var relative = Path.GetRelativePath(sourceDir, filePath);

                if (!availableFiles.TryGetValue(fileName, out var existing))
                {
                    availableFiles[fileName] = filePath;
                    continue;
                }

                // Duplicate: prefer the NormalMode/ copy over others
                var existingRel = Path.GetRelativePath(sourceDir, existing);
                if (relative.StartsWith("NormalMode", StringComparison.OrdinalIgnoreCase)
                    && !existingRel.StartsWith("NormalMode", StringComparison.OrdinalIgnoreCase))
                {
                    availableFiles[fileName] = filePath;
                }
            }

            // Detect year from extracted DLL files
            var extractedYear = DetectYearFromFiles(availableFiles.Keys);
            Logger.Debug($" Detected year: {extractedYear ?? "(none)"}");

            // Capture installed version BEFORE deployment for AppList conversion check
            var (installedVersion, _) = GreenLumaService.DetectInstalledVersion(greenLumaPath);
            Logger.Debug($" Installed GreenLuma version before deployment: {installedVersion}");

            Report("Detecting installation type...");

            // ── Step 3: Determine installation type ─────────────────
            // Use forced method for fresh installs; otherwise auto-detect.
            var installMethod = forcedMethod ?? GreenLumaService.DetectInstallMethod(steamPath, greenLumaPath);
            Logger.Debug($" Install method: {installMethod} (forced: {forcedMethod.HasValue})");

            // ── Step 4: Check user32 condition ──────────────────
            // Only one GreenLuma implementation at a time — if user32.dll
            // will be deployed, skip stealth core files entirely.
            var steamUser32Path = Path.Combine(steamPath, "user32.dll");
            var hasUser32SfDll = availableFiles.ContainsKey("user32SF.dll");
            var user32AlreadyDeployed = !string.IsNullOrWhiteSpace(steamPath) && File.Exists(steamUser32Path);
            Logger.Debug($" user32SF.dll in ZIP: {hasUser32SfDll}, user32.dll in Steam: {user32AlreadyDeployed}");

            // ── Step 5: Deploy files based on install type ──────
            switch (installMethod)
            {
                case GreenLumaInstallMethod.User32:
                    if (forcedMethod == GreenLumaInstallMethod.User32 && !user32AlreadyDeployed)
                    {
                        // Fresh User32 install: deploy AppListManager.exe + user32 DLL
                        Report("Updating GreenLuma files...");
                        DeployAppListManager(availableFiles, extractedYear, sourceDir, steamPath, result);
                        var mode = user32DeployMode ?? User32DeployMode.User32SF;
                        if (DeployUser32Dll(sourceDir, steamPath, mode))
                            result.User32Deployed = true;
                    }
                    else
                    {
                        // Existing User32 update: deploy user32.dll only
                        Report("Deploying user32.dll...");
                        if (hasUser32SfDll && !string.IsNullOrWhiteSpace(steamPath))
                        {
                            var mode = user32DeployMode ?? User32DeployMode.User32SF;
                            if (DeployUser32Dll(sourceDir, steamPath, mode))
                                result.User32Deployed = true;
                        }
                    }
                    break;

                case GreenLumaInstallMethod.Normal:
                    Report("Updating GreenLuma files...");
                    result.DeployedFiles.AddRange(
                        DeployNormalFiles(availableFiles, extractedYear, sourceDir, steamPath));
                    Logger.Debug($" Deployed {result.DeployedFiles.Count} normal files");
                    break;

                case GreenLumaInstallMethod.StealthAny:
                    Report("Updating GreenLuma files...");
                    result.DeployedFiles.AddRange(
                        DeployStealthFiles(availableFiles, extractedYear, sourceDir, greenLumaPath));
                    Logger.Debug($" Deployed {result.DeployedFiles.Count} stealth files");
                    break;

                default:
                    Logger.Debug($" Skipping file deploy: {installMethod}");
                    break;
            }

            // ── Step 6: Cleanup ───────────────────────────────────
            Report("Cleaning up...");
            try
            {
                if (Directory.Exists(tmpDir))
                    Directory.Delete(tmpDir, true);
                Logger.Debug("TMP directory deleted");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TMP cleanup failed (non-critical)");
            }

            // ── Step 7: Extract and save AppList template from ZIP ──────────
            Report("Saving AppList template...");
            SaveAppListTemplateFromZip(availableFiles, sourceDir, greenLumaPath);

            // ── Step 8: AppList conversion (old format → new INI format) ────
            // Check if we're updating from GreenLuma < 1.8.0 and need to convert
            // Use the version captured BEFORE deployment
            Report("Checking AppList format...");
            if (GreenLumaService.IsAppListConversionNeeded(greenLumaPath, installedVersion))
            {
                Report("Converting AppList to new format...");
                if (GreenLumaService.ConvertAppListFolderToIni(greenLumaPath, out var convertError))
                {
                    Logger.Info("AppList conversion successful");
                    result.DeployedFiles.Add("AppList.ini (converted from AppList folder)");
                }
                else
                {
                    Logger.Warn($"AppList conversion failed: {convertError}");
                    // Non-critical - don't fail the deployment
                }
            }

            result.Success = true;
            Report("Done.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error");
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    // ── ZIP Extraction via SharpCompress ─────────────────────────────

    /// <summary>
    /// Extracts a password-protected ZIP (LZMA/BZip2/Deflate) using SharpCompress.
    /// No external tools needed.
    /// </summary>
    private static bool TryExtractZip(string zipPath, string extractDir, out string error)
    {
        error = string.Empty;

        try
        {
            var readerOptions = new ReaderOptions
            {
                Password = ZipPassword,
                LeaveStreamOpen = false
            };

            using var archive = ArchiveFactory.OpenArchive(zipPath, readerOptions);

            var options = new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
                PreserveFileTime = false
            };

            var entryCount = archive.Entries.Count(e => !e.IsDirectory);
            Logger.Debug($" SharpCompress: {entryCount} entries found in archive");

            archive.WriteToDirectory(extractDir, options);

            var extractedCount = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories).Length;
            Logger.Debug($" SharpCompress: extracted {extractedCount} files to {extractDir}");

            if (extractedCount == 0)
            {
                error = "ZIP archive appears to be empty.";
                return false;
            }

            return true;
        }
        catch (CryptographicException ex)
        {
            Logger.Error(ex, "Wrong password or encrypted entry");
            error = "Failed to decrypt ZIP archive. The password may be incorrect.";
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SharpCompress extraction failed");
            error = $"Failed to extract ZIP: {ex.Message}";
            return false;
        }
    }

    // ── Source directory detection ──────────────────────────────────

    /// <summary>
    /// The ZIP may contain a single root folder (e.g. "GreenLuma_2026_1.7.8-Steam006/").
    /// Walk up to find where the actual files live.
    /// </summary>
    private static string FindSourceDirectory(string extractDir)
    {
        var dir = new DirectoryInfo(extractDir);

        while (true)
        {
            var subDirs = dir.GetDirectories();
            var files = dir.GetFiles();

            Logger.Debug($" FindSourceDirectory: {dir.FullName} — {subDirs.Length} subdirs, {files.Length} files");

            if (subDirs.Length == 1 && files.Length == 0)
            {
                dir = subDirs[0];
                continue;
            }

            return dir.FullName;
        }
    }

    // ── Year detection ──────────────────────────────────────────────

    [GeneratedRegex(@"GreenLuma_(\d{4})_", RegexOptions.IgnoreCase)]
    private static partial Regex YearRegex();

    private static string? DetectYearFromFiles(IEnumerable<string> fileNames)
    {
        foreach (var name in fileNames)
        {
            var match = YearRegex().Match(name);
            if (match.Success)
                return match.Groups[1].Value;
        }
        return null;
    }

    // ── Stealth file deployment ─────────────────────────────────────

    private static readonly string[] StealthTargetFiles =
    [
        "AppListManager.exe",
        "DLLInjector.exe",
        "DLLInjector.ini",
        "GreenLumaSettings_2026.exe",
        "GreenLuma_2026_x64.dll"
    ];

    /// <summary>
    /// Files deployed at the root of the Steam directory for Normal mode.
    /// </summary>
    private static readonly string[] NormalTargetFiles =
    [
        "AppListManager.exe",
        "DLLInjector.exe",
        "DLLInjector.ini",
        "GreenLumaSettings_2026.exe",
        "GreenLuma_2026_x64.dll",
        "GreenLuma_2026_x86.dll"
    ];

    /// <summary>
    /// Copies the stealth core files from the extracted ZIP content to the GreenLuma directory.
    /// Handles year-variant filenames (e.g. GreenLuma_2026_... or GreenLuma_2027_...).
    /// </summary>
    private static List<string> DeployStealthFiles(
        Dictionary<string, string> availableFiles,
        string? extractedYear,
        string sourceDir,
        string greenLumaPath)
    {
        var deployed = new List<string>();

        foreach (var target in StealthTargetFiles)
        {
            var sourceFile = ResolveSourceFile(target, availableFiles, sourceDir, extractedYear);

            if (sourceFile == null)
            {
                Logger.Debug($" Stealth target not found in ZIP: {target}");
                continue;
            }

            var destFileName = ResolveDestFileName(target, extractedYear);
            var destPath = Path.Combine(greenLumaPath, destFileName);

            try
            {
                File.Copy(sourceFile, destPath, overwrite: true);
                deployed.Add(destFileName);
                Logger.Debug($" Deployed: {destFileName} ({sourceFile} → {destPath})");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to copy {destFileName}");
            }
        }

        return deployed;
    }

    // ── Normal (full) file deployment ───────────────────────────

    /// <summary>
    /// Deploy the full Normal-mode installation to the Steam root directory.
    /// Copies individual files, the GreenLumaYYYY_Files directory,
    /// and handles the bin\x86launcher.exe backup and replacement.
    /// </summary>
    private static List<string> DeployNormalFiles(
        Dictionary<string, string> availableFiles,
        string? extractedYear,
        string sourceDir,
        string steamPath)
    {
        var deployed = new List<string>();

        // ── Individual files at Steam root ──────────────────────
        foreach (var target in NormalTargetFiles)
        {
            var sourceFile = ResolveSourceFile(target, availableFiles, sourceDir, extractedYear);
            if (sourceFile == null)
            {
                Logger.Debug($" Normal target not found in ZIP: {target}");
                continue;
            }

            var destFileName = ResolveDestFileName(target, extractedYear);
            var destPath = Path.Combine(steamPath, destFileName);

            try
            {
                File.Copy(sourceFile, destPath, overwrite: true);
                deployed.Add(destFileName);
                Logger.Debug($" Deployed: {destFileName}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to copy {destFileName}");
            }
        }

        // ── GreenLumaYYYY_Files directory ───────────────────────
        var folderName = "GreenLuma2026_Files";
        var resolvedFolder = extractedYear != null
            ? folderName.Replace("2026", extractedYear)
            : folderName;

        var sourceFolder = Path.Combine(sourceDir, resolvedFolder);
        if (!Directory.Exists(sourceFolder))
        {
            // Search subdirectories in case it's nested
            sourceFolder = Directory.GetDirectories(sourceDir, resolvedFolder, SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        if (sourceFolder != null && Directory.Exists(sourceFolder))
        {
            var destFolder = Path.Combine(steamPath, resolvedFolder);
            CopyDirectoryRecursive(sourceFolder, destFolder);
            deployed.Add(resolvedFolder + @"\");
            Logger.Debug($" Deployed directory: {resolvedFolder}");
        }
        else
        {
            Logger.Debug($" Directory not found in ZIP: {resolvedFolder}");
        }

        // ── bin\x86launcher.exe with backup ─────────────────────
        const string launcherName = "x86launcher.exe";
        var launcherSource = ResolveSourceFile(launcherName, availableFiles, sourceDir, extractedYear);

        if (launcherSource == null)
        {
            Logger.Debug("x86launcher.exe not found in ZIP");
        }
        else
        {
            var binDir = Path.Combine(steamPath, "bin");
            var launcherDest = Path.Combine(binDir, launcherName);

            // Backup existing launcher if not already backed up
            if (File.Exists(launcherDest))
            {
                var backupPath = launcherDest + ".og";
                try
                {
                    if (!File.Exists(backupPath))
                    {
                        File.Move(launcherDest, backupPath);
                        deployed.Add($"bin\\{launcherName}.og (renamed from original {launcherName})");
                        Logger.Debug($" Backed up: {launcherName}.og");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Failed to backup {launcherName}");
                }
            }

            Directory.CreateDirectory(binDir);

            try
            {
                File.Copy(launcherSource, launcherDest, overwrite: true);
                deployed.Add($"bin\\{launcherName}");
                Logger.Debug($" Deployed: bin\\{launcherName}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to copy {launcherName}");
            }
        }

        return deployed;
    }

    /// <summary>
    /// Recursively copies a directory and its contents.
    /// </summary>
    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, dest);
        }
    }

    private static string? ResolveSourceFile(
        string target,
        Dictionary<string, string> availableFiles,
        string sourceDir,
        string? extractedYear)
    {
        // Exact match first
        if (availableFiles.TryGetValue(target, out var exactPath))
        {
            Logger.Debug($" Resolved '{target}' via exact match");
            return exactPath;
        }

        // Year-variant: replace "2026" in the template with detected year
        if (extractedYear != null && target.Contains("2026"))
        {
            var yearVariant = target.Replace("2026", extractedYear);
            if (availableFiles.TryGetValue(yearVariant, out var yearPath))
            {
                Logger.Debug($" Resolved '{target}' → '{yearVariant}' via year substitution");
                return yearPath;
            }

            // Also try a direct file-scan in the source dir (case-insensitive)
            var directPath = Path.Combine(sourceDir, yearVariant);
            if (File.Exists(directPath))
            {
                Logger.Debug($" Resolved '{target}' → '{yearVariant}' via direct scan");
                return directPath;
            }
        }

        Logger.Debug($" Could not resolve source file for target: {target}");
        return null;
    }

    private static string ResolveDestFileName(string target, string? extractedYear)
    {
        if (extractedYear != null && target.Contains("2026"))
            return target.Replace("2026", extractedYear);
        return target;
    }

    // ── user32.dll deployment ───────────────────────────────────────

    /// <summary>
    /// Deploys the user32 DLL variant to the Steam folder based on the selected mode.
    /// </summary>
    /// <param name="sourceDir">Extracted ZIP root.</param>
    /// <param name="steamPath">Steam directory (destination).</param>
    /// <param name="mode">
    /// <see cref="User32DeployMode.User32"/> — copies a plain user32.dll if found,
    /// falling back to the SF variant. <see cref="User32DeployMode.User32SF"/>
    /// — copies user32SF.dll and renames to user32.dll.
    /// </param>
    private static bool DeployUser32Dll(string sourceDir, string steamPath, User32DeployMode mode)
    {
        string? user32Source = null;

        if (mode == User32DeployMode.User32)
        {
            // Try to find a plain user32.dll first
            user32Source = Directory.GetFiles(sourceDir, "user32.dll", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (user32Source != null)
                Logger.Debug(" Found plain user32.dll in archive");
        }

        // Fall back to user32SF.dll (works for both modes)
        if (user32Source == null)
        {
            user32Source = Directory.GetFiles(sourceDir, "user32SF.dll", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (user32Source != null)
                Logger.Debug(" Using user32SF.dll as source (renaming to user32.dll)");
        }

        if (user32Source == null)
        {
            Logger.Debug("No user32 DLL variant found in extracted files");
            return false;
        }

        var destPath = Path.Combine(steamPath, "user32.dll");

        try
        {
            File.Copy(user32Source, destPath, overwrite: true);
            Logger.Debug($" Deployed: user32.dll ({Path.GetFileName(user32Source)} → {destPath})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to deploy user32.dll");
            return false;
        }
    }

    /// <summary>
    /// Deploys AppListManager.exe to the Steam folder.
    /// </summary>
    private static void DeployAppListManager(
        Dictionary<string, string> availableFiles,
        string? extractedYear,
        string sourceDir,
        string steamPath,
        GreenLumaDeploymentResult result)
    {
        const string target = "AppListManager.exe";
        var sourceFile = ResolveSourceFile(target, availableFiles, sourceDir, extractedYear);

        if (sourceFile == null)
        {
            Logger.Debug("AppListManager.exe not found in ZIP");
            return;
        }

        var destPath = Path.Combine(steamPath, target);

        try
        {
            File.Copy(sourceFile, destPath, overwrite: true);
            result.DeployedFiles.Add(target);
            Logger.Debug($" Deployed: {target}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to copy {target}");
        }
    }

    /// <summary>
    /// Extracts the AppList template from the ZIP (NormalMode/AppList/AppList.ini) 
    /// and saves it as AppList.template.ini for future AppList generation.
    /// </summary>
    private static void SaveAppListTemplateFromZip(
        Dictionary<string, string> availableFiles,
        string sourceDir,
        string greenLumaPath)
    {
        try
        {
            // The template is in NormalMode/AppList/AppList.ini
            const string templateRelativePath = "NormalMode/AppList/AppList.ini";
            var templateSource = availableFiles.Keys
                .FirstOrDefault(k => k.EndsWith("AppList.ini", StringComparison.OrdinalIgnoreCase) 
                    && availableFiles[k].Contains("NormalMode", StringComparison.OrdinalIgnoreCase)
                    && availableFiles[k].Contains("AppList", StringComparison.OrdinalIgnoreCase));

            if (templateSource == null)
            {
                // Try direct lookup
                var directPath = Path.Combine(sourceDir, templateRelativePath);
                if (File.Exists(directPath))
                {
                    templateSource = Path.GetFileName(directPath);
                }
            }

            if (templateSource != null && availableFiles.TryGetValue(templateSource, out var templatePath))
            {
                var templateContent = File.ReadAllText(templatePath);
                
                // Save as template file (keep original format with all commented entries)
                var appListFolder = Path.Combine(greenLumaPath, "AppList");
                Directory.CreateDirectory(appListFolder);
                var templateDestPath = Path.Combine(appListFolder, "AppList.template.ini");
                
                File.WriteAllText(templateDestPath, templateContent);
                Logger.Debug($" Saved AppList template to {templateDestPath}");
            }
            else
            {
                Logger.Debug(" AppList template not found in ZIP");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save AppList template from ZIP");
        }
    }
}
