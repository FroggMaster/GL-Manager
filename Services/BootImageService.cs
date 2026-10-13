using System.IO;
using System.Windows.Media.Imaging;
using GreenLuma_Manager.Models;

namespace GreenLuma_Manager.Services;

/// <summary>
/// Resolves the boot image shown while GreenLuma starts. A user-supplied image in
/// the GreenLuma folder takes priority over one in the GreenLumaYYYY_Files folder,
/// and the embedded default is used when neither exists. The image is rendered by
/// this application, so it does not depend on DLLInjector.
/// </summary>
public static class BootImageService
{
    private const string EmbeddedResourceName = "GreenLuma_Manager.Resources.BootImage_GLManager.png";

    private static readonly string[] ImageExtensions =
        [".bmp", ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".webp", ".tif", ".tiff"];

    /// <summary>
    /// Loads the boot image to display, or null when none is available.
    /// </summary>
    public static BitmapSource? Load(Config config, GreenLumaInstallMethod method)
    {
        var customPath = FindCustomBootImage(config, method);
        if (customPath != null)
        {
            var custom = TryLoadFromFile(customPath);
            if (custom != null)
            {
                Logger.Debug($"Boot image: using custom image '{customPath}'");
                return custom;
            }
        }

        var embedded = LoadEmbedded();
        if (embedded != null)
        {
            Logger.Debug("Boot image: using the embedded default");
            return embedded;
        }

        Logger.Warn("Boot image: no custom or embedded image available");
        return null;
    }

    /// <summary>
    /// Finds a user-supplied boot image. The GreenLuma folder is checked first,
    /// then each GreenLumaYYYY_Files folder. Returns null when none is found.
    /// </summary>
    public static string? FindCustomBootImage(Config config, GreenLumaInstallMethod method)
    {
        var basePath = method switch
        {
            GreenLumaInstallMethod.Normal => config.SteamPath,
            GreenLumaInstallMethod.StealthAny => config.GreenLumaPath,
            GreenLumaInstallMethod.User32 => config.SteamPath,
            _ => config.GreenLumaPath
        };

        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
            return null;

        // 1. GreenLuma folder.
        var direct = FindBootImageFile(basePath);
        if (direct != null)
            return direct;

        // 2. GreenLumaYYYY_Files folder(s).
        try
        {
            foreach (var filesDir in Directory.GetDirectories(basePath, "GreenLuma*_Files"))
            {
                var inFolder = FindBootImageFile(filesDir);
                if (inFolder != null)
                    return inFolder;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Boot image: could not scan '{basePath}' for GreenLuma*_Files folders: {ex.Message}");
        }

        return null;
    }

    private static string? FindBootImageFile(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return null;

            return Directory.EnumerateFiles(directory)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith("BootImage", StringComparison.OrdinalIgnoreCase) ||
                           name.StartsWith("BootImg", StringComparison.OrdinalIgnoreCase);
                })
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Boot image: could not scan '{directory}': {ex.Message}");
            return null;
        }
    }

    private static BitmapSource? TryLoadFromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Decode(stream);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Boot image: failed to load '{path}': {ex.Message}");
            return null;
        }
    }

    private static BitmapSource? LoadEmbedded()
    {
        try
        {
            using var stream = typeof(BootImageService).Assembly
                .GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
            {
                Logger.Warn($"Boot image: embedded resource '{EmbeddedResourceName}' was not found");
                return null;
            }

            return Decode(stream);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Boot image: failed to load the embedded default: {ex.Message}");
            return null;
        }
    }

    private static BitmapSource Decode(Stream stream)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
