using System.IO;
using System.Windows;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager;

public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Logger.Info("Application starting up");
        base.OnStartup(e);
        try
        {
            PluginService.Initialize();
            PluginService.OnApplicationStartup();

            var config = ConfigService.Load();

            if (e.Args.Length > 0)
                foreach (var arg in e.Args)
                    if (string.Equals(arg, "--launch-greenluma", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info("--launch-greenluma argument detected, launching GreenLuma");
                        try
                        {
                            GreenLumaService.LaunchGreenLumaAsync(config).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Failed to launch GreenLuma from command line");
                        }

                        Shutdown();
                        return;
                    }

            var profiles = ProfileService.LoadAll();
            var valid = new HashSet<string>(profiles
                .SelectMany(p => p.Games)
                .Where(g => !string.IsNullOrWhiteSpace(g.AppId))
                .Select(g => g.AppId));
            IconCacheService.DeleteUnusedIcons(valid);
            _ = Task.Run(() => WarmupIconsAsync(profiles));
            _ = SearchService.PrefetchAsync(config);
            _ = Task.Run(() => { _ = SteamService.Instance; });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during application startup");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("Application shutting down");
        try
        {
            PluginService.OnApplicationShutdown();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during application shutdown");
        }

        base.OnExit(e);
    }

    private static async Task WarmupIconsAsync(List<Profile> profiles)
    {
        Logger.Debug($"WarmupIconsAsync started for {profiles.Count} profiles");
        var totalGames = profiles.SelectMany(p => p.Games).Count();
        try
        {
            var semaphore = new SemaphoreSlim(6);
            var tasks = new List<Task>();
            foreach (var profile in profiles)
            {
                var changed = false;
                foreach (var game in profile.Games)
                {
                    if (string.IsNullOrWhiteSpace(game.AppId))
                        continue;
                    var cached = IconCacheService.GetCachedIconPath(game.AppId);
                    if (string.IsNullOrEmpty(cached))
                    {
                        await semaphore.WaitAsync();
                        var t = Task.Run(async () =>
                        {
                            try
                            {
                                string? path = null;
                                if (!string.IsNullOrWhiteSpace(game.IconUrl))
                                    path = await IconCacheService.DownloadAndCacheIconAsync(game.AppId,
                                        game.IconUrl);

                                if (string.IsNullOrEmpty(path))
                                {
                                    await SearchService.FetchIconUrlAsync(game);
                                    if (!string.IsNullOrWhiteSpace(game.IconUrl))
                                        path = await IconCacheService.DownloadAndCacheIconAsync(game.AppId,
                                            game.IconUrl);
                                }

                                if (!string.IsNullOrEmpty(path))
                                {
                                    game.IconUrl = path;
                                    changed = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex, $"Failed to warm up icon for AppId {game.AppId}");
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        });
                        tasks.Add(t);
                    }
                    else if (!string.IsNullOrWhiteSpace(game.IconUrl) && !File.Exists(cached))
                    {
                        await semaphore.WaitAsync();
                        var t2 = Task.Run(async () =>
                        {
                            try
                            {
                                var path =
                                    await IconCacheService.DownloadAndCacheIconAsync(game.AppId, game.IconUrl);
                                if (!string.IsNullOrEmpty(path))
                                {
                                    game.IconUrl = path;
                                    changed = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error(ex, $"Failed to refresh cached icon for AppId {game.AppId}");
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        });
                        tasks.Add(t2);
                    }
                }

                await Task.WhenAll(tasks);
                if (changed)
                    try
                    {
                        ProfileService.Save(profile);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"Failed to save profile after icon warmup: {profile.Name}");
                    }

                tasks.Clear();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during icon warmup");
        }
        finally
        {
            Logger.Debug($"WarmupIconsAsync completed for {profiles.Count} profiles, {totalGames} games");
        }
    }
}