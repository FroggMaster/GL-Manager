using System.Collections.Concurrent;
using System.IO;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Controllers;

public class AppListController
{
    private readonly ProfileController _profileController;
    private readonly GameListController _gameListController;
    private readonly GreenLumaLauncher _launcher;
    private readonly NotificationManager _notificationManager;

    public AppListController(
        ProfileController profileController,
        GameListController gameListController,
        GreenLumaLauncher launcher,
        NotificationManager notificationManager)
    {
        _profileController = profileController;
        _gameListController = gameListController;
        _launcher = launcher;
        _notificationManager = notificationManager;
    }

    public async Task<ImportResult> ImportExistingAppListAsync(Config config)
    {
        var result = new ImportResult();

        var steamAppListPath = PathDetector.IsValidDirectory(config.SteamPath)
            ? Path.Combine(config.SteamPath!, "AppList")
            : null;
        var greenLumaAppListPath = PathDetector.IsValidDirectory(config.GreenLumaPath)
            ? Path.Combine(config.GreenLumaPath!, "AppList")
            : null;

        var steamHasAppList = steamAppListPath != null && Directory.Exists(steamAppListPath) &&
                              Directory.GetFiles(steamAppListPath, "*.txt").Length > 0;
        var greenLumaHasAppList = greenLumaAppListPath != null && Directory.Exists(greenLumaAppListPath) &&
                                  Directory.GetFiles(greenLumaAppListPath, "*.txt").Length > 0;

        if (!steamHasAppList && !greenLumaHasAppList)
        {
            Logger.Info("No existing AppList found in Steam or GreenLuma folders");
            result.FoundAppList = false;
            return result;
        }

        result.FoundAppList = true;
        result.FoundInSteamFolder = steamHasAppList;

        var appListToImport = steamHasAppList ? steamAppListPath! : greenLumaAppListPath!;
        Logger.Info($"Importing AppList from '{appListToImport}'");

        var appIds = new HashSet<string>();
        try
        {
            var files = Directory.GetFiles(appListToImport, "*.txt");
            foreach (var file in files)
            {
                var appId = (await File.ReadAllTextAsync(file)).Trim();
                if (!string.IsNullOrWhiteSpace(appId))
                    appIds.Add(appId);
            }

            Logger.Info($"Found {files.Length} .txt files, extracted {appIds.Count} unique app IDs");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to read AppList from '{appListToImport}'");
            return result;
        }

        if (appIds.Count == 0)
        {
            Logger.Info("AppList contained no valid app IDs");
            return result;
        }

        result.AppIds = [..appIds];
        result.HasSteamWarning = steamHasAppList;
        Logger.Info($"Import result: {appIds.Count} app IDs found (SteamFolder={steamHasAppList})");
        return result;
    }

    public async Task ResolveAndImportAppsAsync(List<string> appIds, Profile profile, IProgress<AppListProgressReport>? progress = null)
    {
        var allAppIds = new HashSet<string>(appIds);
        var allFoundDepotIds = new HashSet<string>();

        Logger.Info($"ResolveAndImportAppsAsync: starting with {appIds.Count} app IDs for profile '{profile.Name}'");
        Logger.Debug($"Input app IDs: [{string.Join(", ", appIds)}]");

        progress?.Report(new AppListProgressReport
        {
            Status = "Fetching package info...",
            IsIndeterminate = true,
            Current = 0,
            Total = appIds.Count
        });

        var packageInfos = new ConcurrentDictionary<string, AppPackageInfo?>();
        var semaphore = new SemaphoreSlim(6);
        var tasks = new List<Task>();
        var completedPackageFetches = 0;
        var packageFetchSkipped = 0;

        foreach (var id in appIds)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var info = await DepotService.FetchAppPackageInfoAsync(id).ConfigureAwait(false);
                    if (info != null)
                    {
                        packageInfos[id] = info;

                        // Only flag as depot if the ID isn't also a known input app ID.
                        // Some depot IDs coincide with other DLC app IDs (e.g. Strange Brigade DLCs
                        // list sibling DLC app IDs as depots), which would incorrectly filter them out.
                        foreach (var depot in info.Depots)
                            if (!allAppIds.Contains(depot))
                                allFoundDepotIds.Add(depot);

                        foreach (var dlcPair in info.DlcDepots)
                        {
                            foreach (var depot in dlcPair.Value)
                                if (!allAppIds.Contains(depot))
                                    allFoundDepotIds.Add(depot);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref packageFetchSkipped);
                        Logger.Debug($"App {id}: FetchAppPackageInfoAsync returned null (likely DLC or non-game)");
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref packageFetchSkipped);
                    Logger.Debug($"App {id}: FetchAppPackageInfoAsync threw: {ex.Message}");
                }
                finally
                {
                    Interlocked.Increment(ref completedPackageFetches);
                    progress?.Report(new AppListProgressReport
                    {
                        Status = $"Fetching package info ({completedPackageFetches}/{appIds.Count})...",
                        IsIndeterminate = false,
                        Current = completedPackageFetches,
                        Total = appIds.Count
                    });
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        tasks.Clear();

        Logger.Info($"Package info fetch complete: {packageInfos.Count} resolved, {packageFetchSkipped} null/errored");

        semaphore = new SemaphoreSlim(6);

        var mainAppIdsToCreate = appIds
            .Where(id => !allFoundDepotIds.Contains(id))
            .ToList();

        var filteredAsDepots = appIds.Count - mainAppIdsToCreate.Count;
        if (filteredAsDepots > 0)
        {
            var filteredIds = appIds.Where(id => allFoundDepotIds.Contains(id)).ToList();
            Logger.Info($"Filtered {filteredAsDepots} app IDs that were identified as depots of other apps: [{string.Join(", ", filteredIds)}]");
        }

        Logger.Info($"Proceeding to resolve details for {mainAppIdsToCreate.Count} app IDs");

        progress?.Report(new AppListProgressReport
        {
            Status = $"Resolving game details (0/{mainAppIdsToCreate.Count})...",
            IsIndeterminate = false,
            Current = 0,
            Total = mainAppIdsToCreate.Count
        });

        var importedGames = new ConcurrentBag<Game>();
        var completedResolutions = 0;

        var resolvedCount = 0;
        var skippedCount = 0;

        foreach (var id in mainAppIdsToCreate)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                var resolved = false;
                try
                {
                    var info = await DepotService.FetchAppPackageInfoAsync(id).ConfigureAwait(false);

                    var game = new Game { AppId = id, Name = string.Empty, Type = "Game" };

                    try
                    {
                        await SearchService.PopulateGameDetailsAsync(game).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"App {id}: PopulateGameDetailsAsync threw: {ex.Message}");
                    }

                    if (info != null)
                    {
                        List<string>? depotsToAssign = null;

                        var parentGameInfo = packageInfos.Values.FirstOrDefault(p => p?.DlcAppIds.Contains(id) == true);

                        if (parentGameInfo != null)
                        {
                            if (parentGameInfo.DlcDepots.TryGetValue(id, out var dlcDepots))
                                depotsToAssign = dlcDepots;
                        }
                        else if (packageInfos.TryGetValue(id, out var selfInfo) && selfInfo != null)
                        {
                            if (selfInfo.Depots.Count > 0)
                                depotsToAssign = selfInfo.Depots;
                            else if (selfInfo.DlcDepots.TryGetValue(id, out var dlcDepots))
                                depotsToAssign = dlcDepots;
                        }

                        if (depotsToAssign != null)
                            game.Depots = depotsToAssign
                                .Where(depotId => allAppIds.Contains(depotId))
                                .ToList();
                    }

                    if (!string.IsNullOrWhiteSpace(game.IconUrl))
                    {
                        try
                        {
                            var path = await IconCacheService.DownloadAndCacheIconAsync(game.AppId, game.IconUrl)
                                .ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(path)) game.IconUrl = path;
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"App {id}: Icon caching failed: {ex.Message}");
                        }
                    }

                    importedGames.Add(game);
                    resolved = true;

                    var nameDisplay = string.IsNullOrEmpty(game.Name) ? "(no name)" : game.Name;
                    Logger.Debug($"App {id}: resolved as '{nameDisplay}' (type={game.Type}, depots={game.Depots.Count})");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"App {id}: unexpected error during resolution: {ex.Message}");
                }
                finally
                {
                    if (resolved) Interlocked.Increment(ref resolvedCount);
                    else Interlocked.Increment(ref skippedCount);

                    Interlocked.Increment(ref completedResolutions);
                    progress?.Report(new AppListProgressReport
                    {
                        Status = $"Resolving game details ({completedResolutions}/{mainAppIdsToCreate.Count})...",
                        IsIndeterminate = false,
                        Current = completedResolutions,
                        Total = mainAppIdsToCreate.Count
                    });
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        Logger.Info($"Resolution complete: {resolvedCount} resolved, {skippedCount} skipped (of {mainAppIdsToCreate.Count} total)");

        progress?.Report(new AppListProgressReport
        {
            Status = "Processing orphan depots...",
            IsIndeterminate = true,
            Current = 0,
            Total = 1
        });

        var newGames = importedGames.ToList();
        var depotsAddedCount = 0;

        foreach (var depotId in allAppIds.Where(id => allFoundDepotIds.Contains(id) && !mainAppIdsToCreate.Contains(id)))
        {
            string? parentAppId = null;

            foreach (var info in packageInfos.Values)
            {
                if (info == null) continue;
                if (info.Depots.Contains(depotId))
                {
                    parentAppId = info.AppId;
                    break;
                }

                foreach (var dlcDepotPair in info.DlcDepots)
                    if (dlcDepotPair.Value.Contains(depotId))
                    {
                        parentAppId = dlcDepotPair.Key;
                        break;
                    }

                if (parentAppId != null) break;
            }

            if (parentAppId != null)
            {
                var parentGame = _gameListController.Games.FirstOrDefault(g => g.AppId == parentAppId) ??
                                 newGames.FirstOrDefault(g => g.AppId == parentAppId);

                if (parentGame != null && !parentGame.Depots.Contains(depotId))
                {
                    parentGame.Depots.Add(depotId);
                    depotsAddedCount++;
                }
            }
        }

        progress?.Report(new AppListProgressReport
        {
            Status = "Adding games to profile...",
            IsIndeterminate = true,
            Current = 0,
            Total = 1
        });

        foreach (var game in newGames)
            if (!profile.Games.Any(g => g.AppId == game.AppId))
                profile.Games.Add(game);

        Logger.Info($"Adding {newGames.Count} games to profile '{profile.Name}' (pre-existing: {profile.Games.Count})");

        ProfileService.Save(profile);

        if (_profileController.CurrentProfile?.Name == "default" ||
            profile.Name == _profileController.CurrentProfile?.Name)
        {
            _gameListController.LoadGames(profile.Games);
            Logger.Info($"Profile '{profile.Name}' loaded into UI: {_gameListController.Games.Count} games displayed");
        }
        else
        {
            Logger.Debug($"Profile '{profile.Name}' saved but not displayed (current profile is '{_profileController.CurrentProfile?.Name}')");
        }

        var totalDepotsIncluded = newGames.Sum(g => g.Depots.Count) + depotsAddedCount;

        Logger.Info($"Done — {newGames.Count} games/DLCs + {totalDepotsIncluded} depots imported from {appIds.Count} total IDs");

        progress?.Report(new AppListProgressReport
        {
            Status = $"Done — added {newGames.Count} games",
            IsIndeterminate = false,
            Current = 1,
            Total = 1
        });

        _notificationManager.ShowToast($"Added {newGames.Count} Games/DLCs & {totalDepotsIncluded} Depots from {appIds.Count} IDs");
    }

    public async Task<int> GenerateAsync(Config? config, Profile? profile)
    {
        Logger.Info("GenerateAsync: starting app list generation");

        if (profile == null || config == null || string.IsNullOrWhiteSpace(config.GreenLumaPath))
        {
            Logger.Info("GenerateAsync: invalid parameters (profile/config missing or no GreenLumaPath)");
            return -1;
        }

        var result = await GreenLumaService.GenerateAppListAsync(profile, config);

        Logger.Info($"GenerateAsync: completed with result={result}");
        return result;
    }

    public bool ValidatePathsForGeneration(Config? config)
    {
        if (config == null)
            return false;

        if (string.IsNullOrWhiteSpace(config.GreenLumaPath))
            return false;

        return _launcher.ValidatePaths(config);
    }
}

public class ImportResult
{
    public bool FoundAppList { get; set; }
    public bool FoundInSteamFolder { get; set; }
    public bool HasSteamWarning { get; set; }
    public List<string> AppIds { get; set; } = [];
}
