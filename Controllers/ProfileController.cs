using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using Microsoft.Win32;

namespace GreenLuma_Manager.Controllers;

public class ProfileController
{
    private readonly ComboBox _cmbProfile;
    private readonly ObservableCollection<string> _profiles;
    private readonly GameListController _gameListController;
    private readonly GreenLumaLauncher _launcher;
    private readonly NotificationManager _notificationManager;

    public Profile? CurrentProfile { get; private set; }
    public Config? Config { get; set; }

    public ProfileController(
        ComboBox cmbProfile,
        ObservableCollection<string> profiles,
        GameListController gameListController,
        GreenLumaLauncher launcher,
        NotificationManager notificationManager)
    {
        _cmbProfile = cmbProfile;
        _profiles = profiles;
        _gameListController = gameListController;
        _launcher = launcher;
        _notificationManager = notificationManager;
    }

    public void LoadProfileList()
    {
        Logger.Info("LoadProfileList: loading profiles");
        _profiles.Clear();

        var availableProfiles = ProfileService.LoadAll().Select(p => p.Name).ToList();
        foreach (var profile in availableProfiles)
            _profiles.Add(profile);

        if (_profiles.Count == 0)
            _profiles.Add("default");

        var lastProfile = Config?.LastProfile ?? "default";

        if (_profiles.Contains(lastProfile))
            _cmbProfile.SelectedItem = lastProfile;
        else
            _cmbProfile.SelectedIndex = 0;

        if (_profiles.Count == 1)
            _profiles.Add("__empty__");

        Logger.Info($"LoadProfileList: loaded {_profiles.Count} profiles");
    }

    public void SelectProfile(string? profileName)
    {
        Logger.Info($"SelectProfile: profile='{profileName ?? "null"}'");
        if (profileName == "__empty__")
            return;

        if (profileName != null)
            LoadProfile(profileName);
    }

    public void LoadProfile(string profileName)
    {
        Logger.Info($"LoadProfile: loading profile='{profileName}'");
        CurrentProfile = ProfileService.Load(profileName);

        if (CurrentProfile == null)
        {
            CurrentProfile = new Profile { Name = profileName };
            ProfileService.Save(CurrentProfile);
        }

        _gameListController.LoadGames(CurrentProfile.Games);

        if (Config != null)
        {
            Config.LastProfile = profileName;
            ConfigService.Save(Config);
        }

        Logger.Info($"LoadProfile: loaded profile='{profileName}' with {CurrentProfile.Games.Count} games");

        _ = ResolveProfileDlcParentsAsync().ContinueWith(
            t => Logger.Error(t.Exception!, "ResolveProfileDlcParentsAsync: unhandled exception"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Links each base game's DLC entries to it (for nesting) and records how many DLCs Steam
    /// reports for it (for the "installed/total" indicator). Also backfills links for DLC entries
    /// saved before the link existed. Runs in the background.
    /// </summary>
    public async Task ResolveProfileDlcParentsAsync()
    {
        if (CurrentProfile == null)
            return;

        var games = _gameListController.Games;
        var changed = false;
        var semaphore = new SemaphoreSlim(8);

        // 1. Link DLCs whose base game is in the profile, and record each base game's DLC total.
        var baseGames = games
            .Where(g => !g.IsVirtual && string.Equals(g.Type, "Game", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var unresolved = games
            .Where(g => !g.IsVirtual &&
                        string.IsNullOrEmpty(g.ParentAppId) &&
                        !string.Equals(g.Type, "Game", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var dlcIdsByBase = new ConcurrentDictionary<string, List<string>>(StringComparer.Ordinal);
        await Task.WhenAll(baseGames.Select(async baseGame =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                dlcIdsByBase[baseGame.AppId] = await SearchService.GetDlcAppIdsFromStoreAsync(baseGame.AppId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"ResolveProfileDlcParentsAsync: failed for appId={baseGame.AppId}");
            }
            finally
            {
                semaphore.Release();
            }
        }));

        foreach (var baseGame in baseGames)
        {
            if (!dlcIdsByBase.TryGetValue(baseGame.AppId, out var dlcIds))
                continue;

            baseGame.TotalDlcCount = dlcIds.Count;

            if (unresolved.Count == 0 || dlcIds.Count == 0)
                continue;

            var dlcSet = dlcIds.ToHashSet(StringComparer.Ordinal);
            foreach (var game in unresolved)
            {
                if (string.IsNullOrEmpty(game.ParentAppId) && dlcSet.Contains(game.AppId))
                {
                    game.ParentAppId = baseGame.AppId;
                    changed = true;
                }
            }
        }

        // 2. Group DLCs whose base game is not in the profile under a virtual parent header.
        var profileAppIds = games
            .Where(g => !g.IsVirtual)
            .Select(g => g.AppId)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = games
            .Where(g => !g.IsVirtual &&
                        !string.Equals(g.Type, "Game", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(g.ParentAppId) || !profileAppIds.Contains(g.ParentAppId)))
            .ToList();

        var resolvedParents = new ConcurrentDictionary<string, SteamParentInfo>(StringComparer.Ordinal);
        await Task.WhenAll(orphans.Select(async dlc =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var info = await SearchService.GetParentInfoAsync(dlc.AppId).ConfigureAwait(false);
                if (info != null)
                    resolvedParents[dlc.AppId] = info;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"ResolveProfileDlcParentsAsync: parent lookup failed for appId={dlc.AppId}");
            }
            finally
            {
                semaphore.Release();
            }
        }));

        // Fall back to Steam PICS for DLCs whose store page is gone (delisted), where the store
        // lookup cannot determine a parent.
        var stillUnresolved = orphans
            .Where(dlc => string.IsNullOrEmpty(dlc.ParentAppId) && !resolvedParents.ContainsKey(dlc.AppId))
            .ToList();

        if (stillUnresolved.Count > 0)
        {
            var detailsMap = await SearchService.FetchGameDetailsBatchAsync(stillUnresolved.Select(d => d.AppId).ToList());
            foreach (var dlc in stillUnresolved)
            {
                if (detailsMap.TryGetValue(dlc.AppId, out var details) &&
                    !string.IsNullOrEmpty(details.ParentAppId) &&
                    details.ParentAppId != dlc.AppId)
                {
                    resolvedParents[dlc.AppId] = new SteamParentInfo(details.ParentAppId, $"App {details.ParentAppId}");
                }
            }
        }

        var virtualParents = new Dictionary<string, Game>(StringComparer.Ordinal);
        foreach (var dlc in orphans)
        {
            resolvedParents.TryGetValue(dlc.AppId, out var parentInfo);
            var parentAppId = string.IsNullOrEmpty(dlc.ParentAppId) ? parentInfo?.AppId : dlc.ParentAppId;
            if (string.IsNullOrEmpty(parentAppId))
                continue;

            if (string.IsNullOrEmpty(dlc.ParentAppId))
            {
                dlc.ParentAppId = parentAppId;
                changed = true;
            }

            if (virtualParents.ContainsKey(parentAppId))
                continue;

            var existing = games.FirstOrDefault(g => g.AppId == parentAppId);
            if (existing == null)
            {
                existing = new Game
                {
                    AppId = parentAppId,
                    Name = parentInfo?.Name ?? $"App {parentAppId}",
                    Type = "Game",
                    IsVirtual = true
                };
                games.Add(existing);
            }

            virtualParents[parentAppId] = existing;
        }

        // Resolve virtual parents' names/icons and DLC totals for display.
        await Task.WhenAll(virtualParents.Values.Where(v => v.IsVirtual).Select(async virtualParent =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await SearchService.PopulateGameDetailsAsync(virtualParent).ConfigureAwait(false);
                virtualParent.TotalDlcCount = (await SearchService.GetDlcAppIdsFromStoreAsync(virtualParent.AppId).ConfigureAwait(false)).Count;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"ResolveProfileDlcParentsAsync: virtual parent details failed for appId={virtualParent.AppId}");
            }
            finally
            {
                semaphore.Release();
            }
        }));

        _gameListController.RefreshDlcGrouping();

        if (!changed)
            return;

        SaveCurrentProfile();
        Logger.Info("ResolveProfileDlcParentsAsync: linked/grouped profile DLCs");
    }

    /// <summary>
    /// Materializes a virtual (grouping-only) base game into a real profile entry.
    /// </summary>
    public void AddVirtualParent(Game game)
    {
        if (game is not { IsVirtual: true })
            return;

        game.IsVirtual = false;
        _gameListController.RefreshDlcGrouping();
        SaveCurrentProfile();
        _notificationManager.ShowToast($"Added '{game.Name}' to your profile");
        Logger.Info($"AddVirtualParent: materialized '{game.Name}' ({game.AppId})");
    }

    public void SaveCurrentProfile()
    {
        Logger.Info($"SaveCurrentProfile: saving profile='{CurrentProfile?.Name ?? "null"}'");
        if (CurrentProfile == null)
            return;

        CurrentProfile.Games.Clear();
        foreach (var game in _gameListController.Games)
            if (!game.IsVirtual)
                CurrentProfile.Games.Add(game);

        ProfileService.Save(CurrentProfile);
        Logger.Info($"SaveCurrentProfile: saved profile='{CurrentProfile.Name}' with {CurrentProfile.Games.Count} games");
    }

    /// <summary>
    /// Discovers every DLC for the given game and adds the ones missing from the active
    /// profile. DLCs are enumerated from the parent game relationship (PICS listofdlc with a
    /// Steam store fallback) rather than by name, so DLCs whose names differ from the game
    /// are still found. Existing entries are never duplicated.
    /// </summary>
    public async Task<Game?> AddAllDlcsAsync(Game game)
    {
        if (game == null)
            return null;

        if (CurrentProfile == null)
        {
            _notificationManager.ShowToast("No profile selected", false);
            return null;
        }

        Logger.Info($"AddAllDlcsAsync: game='{game.Name}' ({game.AppId})");

        List<string> dlcIds;
        try
        {
            dlcIds = await SearchService.GetDlcAppIdsAsync(game.AppId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"AddAllDlcsAsync: DLC lookup failed for appId={game.AppId}");
            _notificationManager.ShowToast($"Failed to look up DLC for '{game.Name}'", false);
            return null;
        }

        if (dlcIds.Count == 0)
        {
            _notificationManager.ShowToast($"No DLC found for '{game.Name}'", false);
            return null;
        }

        var existing = _gameListController.Games.Select(g => g.AppId).ToHashSet(StringComparer.Ordinal);
        var newIds = dlcIds.Where(id => !existing.Contains(id)).ToList();

        if (newIds.Count == 0)
        {
            _notificationManager.ShowToast($"All {dlcIds.Count} DLC(s) for '{game.Name}' are already in your profile", false);
            return null;
        }

        List<Game> dlcGames;
        try
        {
            dlcGames = await SearchService.ResolveDlcGamesAsync(newIds);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"AddAllDlcsAsync: resolving DLC details failed for appId={game.AppId}");
            dlcGames = newIds
                .Select(id => new Game { AppId = id, Name = $"App {id}", Type = "DLC" })
                .ToList();
        }

        foreach (var dlc in dlcGames)
            dlc.ParentAppId = game.AppId;

        var addedDlcs = _gameListController.AddGames(dlcGames);
        SaveCurrentProfile();

        var word = dlcGames.Count == 1 ? "DLC" : "DLCs";
        _notificationManager.ShowToast($"Added {dlcGames.Count} {word} for '{game.Name}'");
        Logger.Info($"AddAllDlcsAsync: added {dlcGames.Count} DLC(s) for '{game.AppId}'");

        // Fill in icons after the entries are visible; keep unresolved entries typed as DLC.
        _ = Task.Run(async () =>
        {
            try
            {
                await SearchService.FetchIconUrlsAsync(dlcGames);
                foreach (var dlc in dlcGames)
                    if (dlc.Name == $"App {dlc.AppId}")
                        dlc.Type = "DLC";
            }
            catch (Exception ex)
            {
                Logger.Debug($"AddAllDlcsAsync: background icon fetch failed: {ex.Message}");
            }
        });

        return addedDlcs.LastOrDefault();
    }

    public string CreateProfile()
    {
        Logger.Info("CreateProfile: opening create profile dialog");
        var dialog = new CreateProfileDialog
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        };

        if (dialog.ShowDialog() != true || dialog.Result == null)
        {
            Logger.Info("CreateProfile: dialog cancelled");
            return string.Empty;
        }

        var newProfile = dialog.Result;

        _profiles.Remove("__empty__");
        _profiles.Add(newProfile.Name);

        ProfileService.Save(newProfile);

        _cmbProfile.SelectedItem = newProfile.Name;
            Logger.Info($"CreateProfile: created profile='{newProfile.Name}'");
        return newProfile.Name;
    }

    public void DeleteProfile(string? profileName)
    {
        Logger.Info($"DeleteProfile: attempting to delete profile='{profileName ?? "null"}'");
        if (string.IsNullOrWhiteSpace(profileName) || profileName == "default")
        {
            Logger.Warn("DeleteProfile: cannot delete default profile");
            _notificationManager.ShowToast("Cannot delete the default profile", false);
            return;
        }

        var result = CustomMessageBox.Show(
            $"Delete profile '{profileName}'?\n\nThis cannot be undone.",
            "Delete Profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            Logger.Info($"DeleteProfile: user cancelled deletion of profile='{profileName}'");
            return;
        }

        ProfileService.Delete(profileName);
        _profiles.Remove(profileName);

        _cmbProfile.SelectedItem = "default";
        _notificationManager.ShowToast($"Profile '{profileName}' deleted");
        Logger.Info($"DeleteProfile: deleted profile='{profileName}'");
    }

    public void ImportProfile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files|*.json|All files|*.*",
            Title = "Import Profile"
        };

        if (dialog.ShowDialog() != true)
            return;

        var profile = ProfileService.Import(dialog.FileName);

        if (profile == null)
        {
            _notificationManager.ShowToast("Failed to import profile", false);
            return;
        }

        foreach (var game in profile.Games)
            game.IconUrl = string.Empty;

        if (!ValidateProfileName(profile.Name))
            return;

        if (_profiles.Contains(profile.Name))
        {
            var overwriteResult = CustomMessageBox.Show(
                $"Profile '{profile.Name}' already exists. Overwrite?",
                "Import Profile",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (overwriteResult != MessageBoxResult.Yes)
                return;
        }
        else
        {
            _profiles.Remove("__empty__");
            _profiles.Add(profile.Name);
        }

        ProfileService.Save(profile);

        if (_cmbProfile.SelectedItem?.ToString() == profile.Name)
            LoadProfile(profile.Name);
        else
            _cmbProfile.SelectedItem = profile.Name;

        _notificationManager.ShowToast($"Imported profile '{profile.Name}'");
    }

    public void ExportProfile()
    {
        if (CurrentProfile == null)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "JSON files|*.json|All files|*.*",
            FileName = $"{CurrentProfile.Name}.json",
            Title = "Export Profile"
        };

        if (dialog.ShowDialog() != true)
            return;

        SaveCurrentProfile();

        var exportProfile = ProfileService.Load(CurrentProfile.Name);
        if (exportProfile != null)
        {
            ProfileService.Export(exportProfile, dialog.FileName);
            _notificationManager.ShowToast($"Exported '{CurrentProfile.Name}'");
        }
        else
        {
            _notificationManager.ShowToast("Failed to export profile", false);
        }
    }

    private static bool ValidateProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName) || profileName.Length > 50)
        {
            return false;
        }

        if (profileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            profileName.Contains('/') || profileName.Contains('\\'))
        {
            return false;
        }

        return true;
    }
}
