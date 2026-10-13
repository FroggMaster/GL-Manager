using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Utilities;
using Newtonsoft.Json.Linq;

namespace GreenLuma_Manager.Services;

public class CacheEntry<T>
{
    public DateTime Expiry { get; set; }
    public T Data { get; set; } = default!;
}

public static class SteamApiCache
{
    internal static readonly ConcurrentDictionary<string, CacheEntry<object>> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<object>> TaskCache = new();
    private static readonly TimeSpan CacheDurationLocal = TimeSpan.FromMinutes(30);

    public static async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> fetchFunc)
    {
        if (Cache.TryGetValue(key, out var entry))
            if (DateTime.Now < entry.Expiry && entry.Data is T cachedVal)
                return cachedVal;

        var task = TaskCache.GetOrAdd(key, _ => FetchAndCacheAsync(key, fetchFunc));
        try
        {
            var result = await task.ConfigureAwait(false);
            return (T)result;
        }
        finally
        {
            TaskCache.TryRemove(key, out _);
        }
    }

    private static async Task<object> FetchAndCacheAsync<T>(string key, Func<Task<T>> fetchFunc)
    {
        var data = await fetchFunc().ConfigureAwait(false);

        if (data != null)
            Cache[key] = new CacheEntry<object>
            {
                Expiry = DateTime.Now.Add(CacheDurationLocal),
                Data = data
            };

        return data!;
    }
}

public class SearchService
{
    private const string SteamStoreSearchUrl =
        "https://store.steampowered.com/api/storesearch/?term={0}&l=english&cc=US";

    private const string SteamStoreApiUrl = "https://api.steampowered.com/IStoreService/GetAppList/v1/";
    private const int BatchSize = 150;

    /// <summary>
    /// Optional user-provided Steam Web API key (register at steamcommunity.com/dev/apikey) used
    /// to fetch the full Steam app list for local search. The previously shipped key was revoked,
    /// and Valve removed the keyless ISteamApps/GetAppList endpoint, so an empty key disables the
    /// app-list feature (search still works through the Steam store search API).
    /// </summary>
    public static string ApiKey { get; set; } = string.Empty;

    private static readonly HttpClient Client = new();
    private static List<SteamApp>? _appListCache;
    private static readonly SemaphoreSlim AppListLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, GameDetails> DetailsCache = new();
    private static DateTime _cacheExpiry = DateTime.MaxValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static bool _isPrefetching;

    static SearchService()
    {
        Client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        Client.Timeout = TimeSpan.FromSeconds(10);
    }

    private static async Task<List<Game>> SearchStoreAsync(string query)
    {
        Logger.Debug($"SearchStoreAsync: query='{query}'");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var url = string.Format(SteamStoreSearchUrl, Uri.EscapeDataString(query));
            var response = await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            var json = JObject.Parse(response);

            var items = json["items"];
            if (items == null) return [];

            var results = new List<Game>();

            foreach (var item in items)
            {
                var appId = item["id"]?.ToString();
                var name = item["name"]?.ToString();
                var tinyImage = item["tiny_image"]?.ToString();

                if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(name))
                    results.Add(new Game
                    {
                        AppId = appId,
                        Name = name,
                        Type = "Game",
                        IconUrl = tinyImage ?? string.Empty
                    });
            }

            return results;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"SearchStoreAsync: failed for query='{query}'");
            return [];
        }
    }

    private static async Task<List<SteamApp>> GetAppListAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            Logger.Info("GetAppListAsync: no Steam Web API key configured, skipping app list fetch");
            return [];
        }

        Logger.Info("GetAppListAsync: starting app list fetch");

        if (_appListCache != null && DateTime.Now < _cacheExpiry)
            return _appListCache;

        await AppListLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_appListCache != null && DateTime.Now < _cacheExpiry)
                return _appListCache;

            _appListCache = [];
            uint lastAppId = 0;
            const int maxResults = 50000;

            while (true)
            {
                var url =
                    $"{SteamStoreApiUrl}?key={Uri.EscapeDataString(ApiKey)}&include_games=true&include_dlc=true&include_software=true&include_videos=true&include_hardware=true&max_results={maxResults}&last_appid={lastAppId}";

                var response = await Client.GetStringAsync(url).ConfigureAwait(false);
                var json = JObject.Parse(response);
                var apps = json["response"]?["apps"];

                if (apps == null || !apps.Any())
                    break;

                foreach (var app in apps)
                {
                    var appId = app["appid"]?.ToString() ?? string.Empty;
                    var name = app["name"]?.ToString() ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(name))
                        _appListCache.Add(new SteamApp(appId, name));
                }

                var haveMore = json["response"]?["have_more_results"]?.Value<bool>() ?? false;
                if (!haveMore)
                    break;

                var lastAppIdFromResponse = json["response"]?["last_appid"]?.Value<uint>();
                if (lastAppIdFromResponse.HasValue)
                    lastAppId = lastAppIdFromResponse.Value;
                else
                    break;
            }

            _cacheExpiry = DateTime.Now.Add(CacheDuration);
            _isPrefetching = false;
            Logger.Info($"GetAppListAsync: complete - {_appListCache.Count} apps cached");
            return _appListCache;
        }
        catch (Exception ex)
        {
            _isPrefetching = false;
            Logger.Error(ex, "GetAppListAsync: failed to fetch app list");
            _appListCache = [];
            _cacheExpiry = DateTime.Now.Add(TimeSpan.FromMinutes(5));
            return _appListCache;
        }
        finally
        {
            AppListLock.Release();
        }
    }

    public static async Task PrefetchAsync(Config config)
    {
        ApiKey = config.SteamApiKey ?? string.Empty;

        if (_isPrefetching || (_appListCache != null && DateTime.Now < _cacheExpiry) || !config.PrefetchAppList)
            return;

        Logger.Info("PrefetchAsync: starting background prefetch");
        _isPrefetching = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await GetAppListAsync().ConfigureAwait(false);
            }
        catch (Exception ex)
        {
            _isPrefetching = false;
            Logger.Error(ex, "PrefetchAsync: background prefetch failed");
        }
        });
    }

    /// <summary>
    /// Clears the cached app list so a newly configured API key takes effect on the next fetch.
    /// </summary>
    public static void ResetAppListCache()
    {
        _appListCache = null;
        _cacheExpiry = DateTime.MaxValue;
    }

    public static async Task<List<Game>> SearchAsync(string query, int maxResults = 50)
    {
        Logger.Debug($"SearchAsync: query='{query}', maxResults={maxResults}");

        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        try
        {
            var queryLower = query.ToLower();
            var cacheKey = $"search:{queryLower}:{maxResults}";

            var cached = await SteamApiCache.GetOrAddAsync(cacheKey, async () =>
            {
                if (uint.TryParse(query, out _))
                {
                    var detailsMap = await FetchGameDetailsBatchAsync([query]).ConfigureAwait(false);
                    if (detailsMap.TryGetValue(query, out var details) &&
                        !string.IsNullOrEmpty(details.Name))
                        return
                        [
                            new Game
                            {
                                AppId = query,
                                Name = details.Name,
                                Type = details.Type,
                                IconUrl = string.Empty
                            }
                        ];
                }

                var storeTask = SearchStoreAsync(query);

                var appList = _appListCache;
                List<Game> localResults = [];

                if (appList is { Count: > 0 } && DateTime.Now < _cacheExpiry)
                    localResults = appList
                        .Select(app => (app, score: CalculateScore(app.Name, queryLower)))
                        .Where(x => x.score > 0)
                        .OrderByDescending(x => x.score)
                        .ThenBy(x => x.app.Name.Length)
                        .Take(maxResults)
                        .Select(x => new Game
                        {
                            AppId = x.app.AppId,
                            Name = x.app.Name,
                            Type = "Game"
                        })
                        .ToList();

                var smartResults = await storeTask.ConfigureAwait(false);

                var finalResults = new List<Game>(smartResults);
                var existingIds = new HashSet<string>(smartResults.Select(g => g.AppId));

                foreach (var game in localResults)
                {
                    if (finalResults.Count >= maxResults) break;

                    if (!existingIds.Contains(game.AppId))
                    {
                        finalResults.Add(game);
                        existingIds.Add(game.AppId);
                    }
                }

                await AppendMatchedGameDlcsAsync(finalResults).ConfigureAwait(false);

                return finalResults;
            });

            return
            [
                .. cached.Select(g =>
                {
                    var clone = CloneGame(g);
                    foreach (var dlc in g.Dlcs)
                        clone.Dlcs.Add(CloneGame(dlc));
                    return clone;
                })
            ];
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"SearchAsync: failed for query='{query}'");
            return [];
        }
    }

    /// <summary>
    /// Copies the persisted fields of a search result so callers never mutate the cached objects.
    /// </summary>
    private static Game CloneGame(Game g) => new()
    {
        AppId = g.AppId,
        Name = g.Name,
        Type = g.Type,
        IconUrl = g.IconUrl
    };

    private static int CalculateScore(string appName, string query)
    {
        if (string.IsNullOrEmpty(appName))
            return 0;

        var nameLower = appName.ToLower();
        var score = 0;

        if (nameLower == query)
            return 10000;

        if (nameLower.StartsWith(query))
            score += 5000;

        var nameWords = nameLower.Split([' ', '-', ':', '_', '™', '®'], StringSplitOptions.RemoveEmptyEntries);
        var queryWords = query.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        if (nameWords.Length > 0 && nameWords[0].StartsWith(query))
            score += 3000;

        var matchingWords = queryWords.Count(queryWord => nameWords.Any(w => w.StartsWith(queryWord)));

        if (queryWords.Length > 1 && matchingWords == queryWords.Length)
            score += 2000;

        if (nameLower.Contains(query))
            score += 1000;

        var lengthPenalty = Math.Max(0, (appName.Length - query.Length) * 50);
        score -= lengthPenalty;

        if (HasWordBoundaryMatch(nameLower, query))
            score += 500;

        if (ContainsAllCharsInOrder(nameLower, query))
            score += 100;

        return Math.Max(0, score);
    }

    private static bool HasWordBoundaryMatch(string name, string query)
    {
        var words = name.Split([' ', '-', ':', '_'], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => w.StartsWith(query));
    }

    private static bool ContainsAllCharsInOrder(string text, string chars)
    {
        var charIndex = 0;
        foreach (var c in text)
            if (charIndex < chars.Length && c == chars[charIndex])
                charIndex++;
        return charIndex == chars.Length;
    }

    public static async Task<Dictionary<string, GameDetails>> FetchGameDetailsBatchAsync(List<string> appIds)
    {
        var results = new Dictionary<string, GameDetails>();
        var uncachedAppIds = new List<string>();

        foreach (var appId in appIds)
        {
            if (DetailsCache.TryGetValue(appId, out var memDetails))
            {
                results[appId] = memDetails;
                continue;
            }

            var key = $"details:{appId}";
            if (SteamApiCache.Cache.TryGetValue(key, out var entry) &&
                DateTime.Now < entry.Expiry &&
                entry.Data is GameDetails cached)
            {
                results[appId] = cached;
                DetailsCache.TryAdd(appId, cached);
            }
            else
            {
                uncachedAppIds.Add(appId);
            }
        }

        if (uncachedAppIds.Count == 0)
            return results;

        var batches = uncachedAppIds.Chunk(BatchSize).ToList();

        foreach (var batch in batches)
            try
            {
                var validAppIds = batch.Where(id => uint.TryParse(id, out _)).ToList();
                if (validAppIds.Count == 0) continue;

                var batchResults =
                    await SteamService.Instance.GetAppInfoBatchAsync(validAppIds.Select(uint.Parse).ToList())
                        .ConfigureAwait(false);

                var successCount = 0;
                foreach (var (appId, details) in batchResults)
                {
                    var appIdStr = appId.ToString();
                    results[appIdStr] = details;
                    successCount++;

                    var key = $"details:{appIdStr}";
                    SteamApiCache.Cache[key] = new CacheEntry<object>
                    {
                        Expiry = DateTime.Now.Add(TimeSpan.FromMinutes(30)),
                        Data = details
                    };
                    DetailsCache.TryAdd(appIdStr, details);
                }

                var missingCount = 0;
                var missingAppIds = validAppIds.Where(id => !results.ContainsKey(id)).ToList();
                foreach (var appIdStr in missingAppIds)
                {
                    var fallbackDetails = new GameDetails(appIdStr, "Game", $"App {appIdStr}");
                    results[appIdStr] = fallbackDetails;
                    missingCount++;
                }

                // Try to resolve missing DLC/non-game apps via the Steam store API
                if (missingAppIds.Count > 0)
                {
                    var storeDetails = await FetchGameDetailsFromStoreApiAsync(missingAppIds).ConfigureAwait(false);
                    foreach (var (appId, details) in storeDetails)
                    {
                        if (results.TryGetValue(appId, out var existing) && existing.Name == $"App {appId}")
                        {
                            results[appId] = details;
                            Logger.Debug($"Store API resolved app {appId}: '{details.Name}' (type={details.Type})");
                            var key = $"details:{appId}";
                            SteamApiCache.Cache[key] = new CacheEntry<object>
                            {
                                Expiry = DateTime.Now.Add(TimeSpan.FromMinutes(30)),
                                Data = details
                            };
                            DetailsCache.TryAdd(appId, details);
                        }
                    }

                    var storeResolved = storeDetails.Count;
                    if (storeResolved < missingAppIds.Count)
                        Logger.Debug($"Store API: resolved {storeResolved}/{missingAppIds.Count} missing apps; {missingAppIds.Count - storeResolved} remain as fallback");
                }

                Debug.WriteLine($"[SearchService] Batch: {validAppIds.Count} apps, {successCount} from SteamAPI, {missingCount} fallback (null hashes)");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"FetchGameDetailsBatchAsync: SteamAPI batch failed for {batch.Length} apps");
                foreach (var appIdStr in batch)
                    if (!results.ContainsKey(appIdStr))
                        results[appIdStr] = new GameDetails(appIdStr, "Game", $"App {appIdStr}");
            }

        return results;
    }

    /// <summary>Falls back to the Steam store API for app details when PICS returns nothing (common for DLCs).</summary>
    private static async Task<Dictionary<string, GameDetails>> FetchGameDetailsFromStoreApiAsync(List<string> appIds)
    {
        var results = new Dictionary<string, GameDetails>();
        try
        {
            var ids = string.Join(",", appIds);
            var url = $"https://store.steampowered.com/api/appdetails?appids={ids}";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            var json = JObject.Parse(response);

            foreach (var appId in appIds)
            {
                var appData = json[appId];
                if (appData == null) continue;

                var success = appData["success"]?.Value<bool>() ?? false;
                if (!success) continue;

                var data = appData["data"];
                if (data == null) continue;

                var name = data["name"]?.ToString();
                var type = data["type"]?.ToString() ?? "Game";

                if (!string.IsNullOrEmpty(name))
                    results[appId] = new GameDetails(appId, type, name);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "FetchGameDetailsFromStoreApiAsync: Store API unavailable");
        }

        return results;
    }

    /// <summary>
    /// Enumerates the DLC app IDs for a game. The primary source is the Steam PICS
    /// <c>common.extended.listofdlc</c> field (the same source CreamInstaller reads via
    /// SteamCMD) plus depot <c>dlcappid</c> entries. When PICS returns nothing, the Steam
    /// store <c>dlc</c> array is used as a fallback. Results are cached for 30 minutes.
    /// </summary>
    public static async Task<List<string>> GetDlcAppIdsAsync(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !uint.TryParse(appId, out _))
            return [];

        var cacheKey = $"dlcids:{appId}";

        return await SteamApiCache.GetOrAddAsync(cacheKey, async () =>
        {
            var ids = new List<string>();

            try
            {
                var info = await DepotService.FetchAppPackageInfoAsync(appId).ConfigureAwait(false);
                if (info?.DlcAppIds is { Count: > 0 })
                    ids.AddRange(info.DlcAppIds);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"GetDlcAppIdsAsync: PICS lookup failed for appId={appId}");
            }

            if (ids.Count == 0)
                try
                {
                    ids.AddRange(await FetchDlcIdsFromStoreApiAsync(appId).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"GetDlcAppIdsAsync: store lookup failed for appId={appId}");
                }

            var result = ids
                .Where(id => uint.TryParse(id, out _))
                .Distinct()
                .ToList();

            Logger.Debug($"GetDlcAppIdsAsync: appId={appId} -> {result.Count} DLC app ID(s)");
            return result;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Store-only DLC id enumeration (no Steam/PICS dependency). Used by background profile work
    /// where waiting on the Steam connection is undesirable.
    /// </summary>
    public static async Task<List<string>> GetDlcAppIdsFromStoreAsync(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !uint.TryParse(appId, out _))
            return [];

        var cacheKey = $"dlcids_store:{appId}";

        return await SteamApiCache.GetOrAddAsync(cacheKey, async () =>
        {
            try
            {
                return await FetchDlcIdsFromStoreApiAsync(appId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"GetDlcAppIdsFromStoreAsync: failed for appId={appId}");
                return new List<string>();
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the parent game (app id + name) for a DLC from the Steam store <c>fullgame</c> field,
    /// or null when it cannot be determined. Used to group DLCs whose base game is not in the profile.
    /// </summary>
    public static async Task<SteamParentInfo?> GetParentInfoAsync(string dlcAppId)
    {
        if (string.IsNullOrWhiteSpace(dlcAppId) || !uint.TryParse(dlcAppId, out _))
            return null;

        var cacheKey = $"parent:{dlcAppId}";

        return await SteamApiCache.GetOrAddAsync(cacheKey, async () =>
        {
            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={dlcAppId}&l=english&cc=US&filters=basic";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
                var json = JObject.Parse(response);

                var fullgame = json[dlcAppId]?["data"]?["fullgame"];
                var appId = fullgame?["appid"]?.ToString();
                if (string.IsNullOrWhiteSpace(appId))
                    return null;

                var name = fullgame?["name"]?.ToString();
                return new SteamParentInfo(appId, string.IsNullOrWhiteSpace(name) ? $"App {appId}" : name);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"GetParentInfoAsync: failed for appId={dlcAppId}");
                return null;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Reads the <c>data.dlc</c> array from the Steam store appdetails response for a single app.</summary>
    private static async Task<List<string>> FetchDlcIdsFromStoreApiAsync(string appId)
    {
        var ids = new List<string>();

        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=US&filters=basic";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        var json = JObject.Parse(response);

        var dlc = json[appId]?["data"]?["dlc"];
        if (dlc is { Type: JTokenType.Array })
            foreach (var token in dlc)
            {
                var value = token.ToString();
                if (uint.TryParse(value, out _))
                    ids.Add(value);
            }

        return ids;
    }

    /// <summary>Reads name and type for a single app from the Steam store appdetails response.</summary>
    private static async Task<GameDetails?> FetchGameDetailFromStoreApiAsync(string appId)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&cc=US&filters=basic";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            var json = JObject.Parse(response);

            var data = json[appId]?["data"];
            if (data == null)
                return null;

            var name = data["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                return null;

            var type = data["type"]?.ToString() ?? "Game";
            return new GameDetails(appId, SteamService.MapSteamTypeToDisplayType(type), name);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"FetchGameDetailFromStoreApiAsync: failed for appId={appId}");
            return null;
        }
    }

    /// <summary>
    /// Resolves DLC app IDs into <see cref="Game"/> entries with names and real Steam types
    /// (when available). PICS often omits DLC entries, so unresolved IDs are retried through
    /// the Steam store before being kept as a generic "DLC". Icons are populated separately
    /// so callers can show the entries immediately.
    /// </summary>
    public static async Task<List<Game>> ResolveDlcGamesAsync(IEnumerable<string> dlcIds)
    {
        var ids = dlcIds.Where(id => uint.TryParse(id, out _)).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var detailsMap = await FetchGameDetailsBatchAsync(ids).ConfigureAwait(false);

        var games = new List<Game>(ids.Count);
        var unresolved = new List<string>();

        foreach (var id in ids)
        {
            detailsMap.TryGetValue(id, out var details);
            var resolved = details != null && !string.IsNullOrEmpty(details.Name) && details.Name != $"App {id}";

            games.Add(new Game
            {
                AppId = id,
                Name = resolved ? details!.Name : $"App {id}",
                Type = resolved ? details!.Type : "DLC"
            });

            if (!resolved)
                unresolved.Add(id);
        }

        if (unresolved.Count > 0)
        {
            var semaphore = new SemaphoreSlim(6);
            var lookups = unresolved.Select(async id =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    var detail = await FetchGameDetailFromStoreApiAsync(id).ConfigureAwait(false);
                    if (detail == null)
                        return;

                    var game = games.First(g => g.AppId == id);
                    game.Name = detail.Name;
                    game.Type = detail.Type;

                    DetailsCache.TryAdd(id, detail);
                    SteamApiCache.Cache[$"details:{id}"] = new CacheEntry<object>
                    {
                        Expiry = DateTime.Now.Add(TimeSpan.FromMinutes(30)),
                        Data = detail
                    };
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(lookups).ConfigureAwait(false);
        }

        return games;
    }

    /// <summary>
    /// Appends the DLCs of the best-matching base game to the search results. The Steam store
    /// search never returns DLCs, and DLC names often differ from the parent game (for example
    /// Squad's "Canadian Armed Forces"), so discovering them requires the parent relationship.
    /// </summary>
    /// <summary>
    /// Discovers each result's DLCs and nests them under their parent game. DLCs that the Steam
    /// store search returned as standalone entries are pulled out of the flat list so they only
    /// appear (nested) under their parent. Runs for every game in the results, not just the best
    /// match, so the expander works for all games.
    /// </summary>
    private static async Task AppendMatchedGameDlcsAsync(List<Game> results)
    {
        var candidates = results
            .Where(g => string.Equals(g.Type, "Game", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return;

        var dlcGamesByParent = new Dictionary<string, List<Game>>(StringComparer.Ordinal);
        var semaphore = new SemaphoreSlim(8);

        var lookups = candidates.Select(async game =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var dlcIds = await GetDlcAppIdsAsync(game.AppId).ConfigureAwait(false);
                if (dlcIds.Count == 0)
                    return;

                var dlcGames = await ResolveDlcGamesAsync(dlcIds).ConfigureAwait(false);
                if (dlcGames.Count == 0)
                    return;

                lock (dlcGamesByParent)
                    dlcGamesByParent[game.AppId] = dlcGames;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"AppendMatchedGameDlcsAsync: DLC lookup failed for appId={game.AppId}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(lookups).ConfigureAwait(false);

        if (dlcGamesByParent.Count == 0)
            return;

        // Nest each game's DLCs under it and remember their ids so they can be removed from the
        // flat result list (the store search often returns DLCs as standalone "Game" entries).
        var nestedDlcIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var game in candidates)
        {
            if (!dlcGamesByParent.TryGetValue(game.AppId, out var dlcGames))
                continue;

            game.Dlcs.Clear();
            foreach (var dlc in dlcGames)
            {
                game.Dlcs.Add(dlc);
                nestedDlcIds.Add(dlc.AppId);
            }

            Logger.Debug($"AppendMatchedGameDlcsAsync: nested {dlcGames.Count} DLC(s) under '{game.Name}' ({game.AppId})");
        }

        results.RemoveAll(g => nestedDlcIds.Contains(g.AppId));
    }

    public static async Task PopulateGameDetailsAsync(Game game)
    {
        Logger.Debug($"PopulateGameDetailsAsync: appId={game.AppId}");
        var detailsMap = await FetchGameDetailsBatchAsync([game.AppId]).ConfigureAwait(false);
        if (detailsMap.TryGetValue(game.AppId, out var details))
        {
            if (details.Name != $"App {game.AppId}")
                game.Name = details.Name;
            game.Type = details.Type;

            try
            {
                var iconPath = await IconCacheService.CacheIconForGameAsync(details).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(iconPath))
                    game.IconUrl = iconPath;
            }
            catch (Exception ex)
            {
                Logger.Debug($"PopulateGameDetailsAsync: icon caching failed for appId={game.AppId}: {ex.Message}");
            }
        }
    }

    public static async Task FetchIconUrlAsync(Game game)
    {
        if (!string.IsNullOrEmpty(game.IconUrl) && !game.IconUrl.StartsWith("http"))
            return;

        await PopulateGameDetailsAsync(game).ConfigureAwait(false);
    }

    public static async Task FetchIconUrlsAsync(List<Game> games)
    {
        var appIds = games.Select(g => g.AppId).Distinct().ToList();
        var detailsMap = await FetchGameDetailsBatchAsync(appIds).ConfigureAwait(false);

        foreach (var game in games)
            if (detailsMap.TryGetValue(game.AppId, out var details))
            {
                if (!string.IsNullOrEmpty(details.Name) && details.Name != $"App {game.AppId}")
                    game.Name = details.Name;
                game.Type = details.Type;
            }

        var semaphore = new SemaphoreSlim(8);
        var successCount = 0;
        var failCount = 0;
        var tasks = games.Select(async game =>
        {
            if (detailsMap.TryGetValue(game.AppId, out var details))
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    var iconPath = await IconCacheService.CacheIconForGameAsync(details).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        game.IconUrl = iconPath;
                        Interlocked.Increment(ref successCount);
                    }
                    else
                    {
                        Interlocked.Increment(ref failCount);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        Debug.WriteLine($"[SearchService] FetchIconUrlsAsync: {successCount} cached, {failCount} failed for {games.Count} games");
    }

    private record SteamApp(string AppId, string Name);
}

/// <summary>Parent game info resolved from a DLC's Steam store <c>fullgame</c> field.</summary>
public sealed record SteamParentInfo(string AppId, string Name);