using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using GreenLuma_Manager.Utilities;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager.Services;

public class IconCacheService
{
    private static readonly string IconCacheDir = PathDetector.IconsDir;

    private static readonly HttpClient Client = new();

    static IconCacheService()
    {
        Client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        Client.Timeout = TimeSpan.FromSeconds(10);
    }

    public static async Task<string?> DownloadAndCacheIconAsync(string appId, string iconUrl)
    {
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(iconUrl))
            return null;
        if (!iconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            PathDetector.EnsureExists(IconCacheDir);
            var extension = GetImageExtension(iconUrl);
            var filePath = Path.Combine(IconCacheDir, appId + extension);

            if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                return filePath;

            var data = await TryDownloadWithRetries(iconUrl, 3, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (data is { Length: > 0 })
            {
                await File.WriteAllBytesAsync(filePath, data).ConfigureAwait(false);
                return filePath;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public static async Task<string?> CacheIconForGameAsync(GameDetails details)
    {
        return await CacheIconForGameRecursiveAsync(details, 0).ConfigureAwait(false);
    }

    private static async Task<string?> CacheIconForGameRecursiveAsync(GameDetails details, int depth)
    {
        if (depth > 2)
        {
            Logger.Debug($" App {details.AppId}: max recursion depth reached ({depth})");
            return null;
        }

        PathDetector.EnsureExists(IconCacheDir);

        var isDlc = string.Equals(details.Type, "DLC", StringComparison.OrdinalIgnoreCase);
        var cached = GetCachedIconPath(details.AppId, isDlc);
        if (!string.IsNullOrEmpty(cached))
        {
            Logger.Debug($" App {details.AppId}: found cached icon at {cached}");
            return cached;
        }

        Logger.Debug($" App {details.AppId} ({details.Type}, depth={depth}): no cached icon, fetching...");

        var candidates = new List<IconCandidate>();

        if (isDlc)
        {
            if (!string.IsNullOrEmpty(details.HeroHash))
            {
                candidates.Add(new IconCandidate(
                    $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/{details.HeroHash}/hero_capsule.jpg"));
                candidates.Add(new IconCandidate(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/{details.HeroHash}/hero_capsule.jpg"));
            }

            if (!string.IsNullOrEmpty(details.MainHash))
            {
                candidates.Add(new IconCandidate(
                    $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/{details.MainHash}/capsule_616x353.jpg"));
                candidates.Add(new IconCandidate(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/{details.MainHash}/capsule_616x353.jpg"));
            }
            else
            {
                candidates.Add(new IconCandidate(
                    $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/capsule_616x353.jpg"));
                candidates.Add(new IconCandidate(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/capsule_616x353.jpg"));
            }

            if (!string.IsNullOrEmpty(details.HeaderImage))
            {
                candidates.Add(new IconCandidate(
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/{details.HeaderImage}"));
                candidates.Add(new IconCandidate(
                    $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/{details.HeaderImage}"));
                candidates.Add(new IconCandidate(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/{details.HeaderImage}"));
            }

            candidates.Add(new IconCandidate(
                $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/header.jpg"));
            candidates.Add(new IconCandidate(
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/header.jpg"));
            candidates.Add(new IconCandidate(
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/header.jpg"));
        }
        else
        {
            if (!string.IsNullOrEmpty(details.ClientIconHash))
            {
                candidates.Add(new IconCandidate(
                    $"https://shared.fastly.steamstatic.com/community_assets/images/apps/{details.AppId}/{details.ClientIconHash}.ico",
                    64));
                candidates.Add(new IconCandidate(
                    $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{details.AppId}/{details.ClientIconHash}.ico",
                    64));
            }

            if (!string.IsNullOrEmpty(details.HeroHash))
            {
                candidates.Add(new IconCandidate(
                    $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/{details.HeroHash}/hero_capsule.jpg"));
                candidates.Add(new IconCandidate(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/{details.HeroHash}/hero_capsule.jpg"));
            }

            if (!string.IsNullOrEmpty(details.MainHash))
            {
                candidates.Add(new IconCandidate(
                    $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/{details.MainHash}/capsule_616x353.jpg"));
                candidates.Add(new IconCandidate(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/{details.MainHash}/capsule_616x353.jpg"));
            }
            else
            {
                candidates.Add(new IconCandidate(
                    $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/capsule_616x353.jpg"));
                candidates.Add(new IconCandidate(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/capsule_616x353.jpg"));
            }

            if (!string.IsNullOrEmpty(details.HeaderImage))
            {
                candidates.Add(new IconCandidate(
                    $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/{details.HeaderImage}"));
                candidates.Add(new IconCandidate(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/{details.HeaderImage}"));
            }

            candidates.Add(new IconCandidate(
                $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{details.AppId}/header.jpg"));
            candidates.Add(new IconCandidate(
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{details.AppId}/header.jpg"));
        }

        Logger.Debug($" App {details.AppId}: trying {candidates.Count} candidates...");
        foreach (var candidate in candidates)
            try
            {
                Logger.Debug($" App {details.AppId}: attempting {candidate.Url}");
                var data = await TryDownloadWithRetries(candidate.Url, 2, TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
                if (data == null || data.Length == 0)
                {
                    Logger.Debug($" App {details.AppId}: no data from {candidate.Url}");
                    continue;
                }

                if (candidate.MinSize > 0 && !IsValidImageSize(data, candidate.MinSize))
                {
                    Logger.Debug($" App {details.AppId}: image too small from {candidate.Url} (min={candidate.MinSize}px)");
                    continue;
                }

                var extension = GetImageExtension(candidate.Url);
                var filePath = Path.Combine(IconCacheDir, details.AppId + extension);
                await File.WriteAllBytesAsync(filePath, data).ConfigureAwait(false);
                Logger.Debug($" App {details.AppId}: cached icon to {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"App {details.AppId}: failed to download {candidate.Url}");
            }

        if (isDlc && !string.IsNullOrEmpty(details.ParentAppId) && uint.TryParse(details.ParentAppId, out var parentId))
        {
            Logger.Debug($" App {details.AppId}: trying parent {parentId} icon as fallback...");
            try
            {
                var parentDetails = await SteamService.Instance.GetGameDetailsAsync(parentId).ConfigureAwait(false);
                if (parentDetails != null)
                {
                    var parentPath = await CacheIconForGameRecursiveAsync(parentDetails, depth + 1)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(parentPath) && File.Exists(parentPath))
                    {
                        var ext = Path.GetExtension(parentPath);
                        var myPath = Path.Combine(IconCacheDir, details.AppId + ext);
                        File.Copy(parentPath, myPath, true);
                        Logger.Debug($" App {details.AppId}: copied parent icon from {parentPath}");
                        return myPath;
                    }
                }
                else
                {
                    Logger.Debug($" App {details.AppId}: parent {parentId} details not found");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"App {details.AppId}: parent icon fallback failed");
            }
        }

        Logger.Debug($" App {details.AppId}: all candidates exhausted, no icon cached");
        return null;
    }

    private static bool IsValidImageSize(byte[] data, int minSize)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return false;
            var frame = decoder.Frames[0];
            return frame.PixelWidth >= minSize;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<byte[]?> TryDownloadWithRetries(string url, int maxAttempts, TimeSpan delay)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var resp = await Client.GetAsync(url, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return null;
                var data = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                if (data.Length > 0)
                    return data;
            }
            catch
            {
                if (attempt == maxAttempts)
                    break;
            }

            await Task.Delay(delay).ConfigureAwait(false);
        }

        return null;
    }

    public static string? GetCachedIconPath(string appId, bool preferJpg = false)
    {
        if (string.IsNullOrEmpty(appId)) return null;

        try
        {
            if (!Directory.Exists(IconCacheDir)) return null;

            var preferences = preferJpg
                ? new[] { ".jpg", ".jpeg", ".ico", ".png", ".webp" }
                : new[] { ".ico", ".jpg", ".jpeg", ".png", ".webp" };

            foreach (var ext in preferences)
            {
                var filePath = Path.Combine(IconCacheDir, $"{appId}{ext}");
                if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                    return filePath;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public static void DeleteCachedIcon(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return;

        try
        {
            if (!Directory.Exists(IconCacheDir)) return;

            string[] extensions = [".jpg", ".ico", ".jpeg", ".png", ".gif", ".webp"];

            foreach (var ext in extensions)
            {
                var filePath = Path.Combine(IconCacheDir, $"{appId}{ext}");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    break;
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    public static void DeleteUnusedIcons(HashSet<string> validAppIds)
    {
        try
        {
            if (!Directory.Exists(IconCacheDir)) return;
            var files = Directory.GetFiles(IconCacheDir);
            foreach (var file in files)
                try
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!validAppIds.Contains(name))
                        File.Delete(file);
                }
                catch
                {
                    // ignored
                }
        }
        catch
        {
            // ignored
        }
    }


    private static string GetImageExtension(string url)
    {
        try
        {
            var lower = url.ToLower();
            if (lower.Contains(".ico")) return ".ico";
            if (lower.Contains(".jpg") || lower.Contains("jpeg")) return ".jpg";
            if (lower.Contains(".png")) return ".png";
            if (lower.Contains(".gif")) return ".gif";
            if (lower.Contains(".webp")) return ".webp";
            return ".jpg";
        }
        catch
        {
            return ".jpg";
        }
    }

    private record IconCandidate(string Url, int MinSize = 0);
}