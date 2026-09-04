using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager.Controllers;

public class GameListController
{
    private readonly ItemsControl _lstGames;
    private readonly TextBlock _txtGameCount;
    private readonly UIElement _pnlEmptyGames;
    private readonly NotificationManager _notificationManager;
    private ICollectionView? _gamesView;
    private string? _searchFilter;
    private string? _typeFilter;

    public ObservableCollection<Game> Games { get; }
    public string? EditingOriginalName { get; set; }

    public Game? CurrentSelection { get; set; }

    public bool IsFilterActive => !string.IsNullOrWhiteSpace(_searchFilter) || IsTypeFilterActive;
    public bool IsTypeFilterActive => !string.IsNullOrWhiteSpace(_typeFilter) &&
        !string.Equals(_typeFilter, "All", StringComparison.OrdinalIgnoreCase);

    public GameListController(
        ItemsControl lstGames,
        TextBlock txtGameCount,
        UIElement pnlEmptyGames,
        NotificationManager notificationManager)
    {
        _lstGames = lstGames;
        _txtGameCount = txtGameCount;
        _pnlEmptyGames = pnlEmptyGames;
        _notificationManager = notificationManager;

        Games = [];
        _gamesView = CollectionViewSource.GetDefaultView(Games);
        _gamesView.Refresh();
        _lstGames.ItemsSource = _gamesView;
    }

    public void SetSearchFilter(string? searchText)
    {
        Logger.Debug($"SetSearchFilter: filter='{searchText ?? "null"}'");
        _searchFilter = searchText;
        ApplyFilters();
    }

    public void SetTypeFilter(string? type)
    {
        _typeFilter = type;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var hasSearch = !string.IsNullOrWhiteSpace(_searchFilter);
        var hasType = IsTypeFilterActive;

        if (!hasSearch && !hasType)
        {
            _gamesView!.Filter = null;
            _notificationManager.UpdateGameCount(Games.Count);
        }
        else
        {
            var rawFilter = _searchFilter?.Trim();
            var normalizedFilter = !string.IsNullOrWhiteSpace(rawFilter)
                ? NormalizeForSearch(rawFilter)
                : null;

            var typeFilterValue = _typeFilter;

            _gamesView!.Filter = obj =>
            {
                if (obj is not Game game) return false;

                // Apply name search filter
                if (normalizedFilter != null)
                {
                    var normalizedName = NormalizeForSearch(game.Name);
                    if (!normalizedName.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                // Apply type filter
                if (hasType && !string.Equals(game.Type, typeFilterValue, StringComparison.OrdinalIgnoreCase))
                    return false;

                return true;
            };

            var filteredCount = 0;
            foreach (var _ in _gamesView!)
                filteredCount++;
            _notificationManager.UpdateGameCount(filteredCount, true);
        }

        UpdateGameListState();
    }

    /// <summary>
    /// Strips characters that commonly cause search mismatches (apostrophes, etc.)
    /// so that searching "Dragons" matches "Dragon's".
    /// </summary>
    private static string NormalizeForSearch(string text)
    {
        return text
            .Replace("'", "")
            .Replace("’", "")
            .Replace("ʻ", "")
            .Replace("ʼ", "");
    }

    public void ClearGames()
    {
        Games.Clear();
        _notificationManager.UpdateGameCount(Games.Count);
        UpdateGameListState();
    }

    public void AddGame(Game game)
    {
        if (Games.Any(g => g.AppId == game.AppId))
            return;

        // Insert in alphabetical order by name
        var index = 0;
        while (index < Games.Count && string.Compare(Games[index].Name, game.Name, StringComparison.OrdinalIgnoreCase) <= 0)
            index++;

        Games.Insert(index, game);
        _notificationManager.UpdateGameCount(Games.Count);
        UpdateGameListState();
    }

    public void RemoveGame(Game game)
    {
        Games.Remove(game);
        _notificationManager.UpdateGameCount(Games.Count);
        UpdateGameListState();
    }

    public void LoadGames(IEnumerable<Game> games)
    {
        Games.Clear();
        foreach (var game in games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            Games.Add(game);

        // Clear search filter when loading a new set of games
        _searchFilter = null;
        if (_gamesView != null)
            _gamesView.Filter = null;

        _notificationManager.UpdateGameCount(Games.Count);
        UpdateGameListState();
    }

    public void UpdateGameListState()
    {
        var hasItems = Games.Count > 0;

        // Show empty panel only if there are no games at all (not just filtered away)
        _pnlEmptyGames.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ToggleGameCheck(Game game, bool? isChecked)
    {
    }

    public void StartRename(Game game)
    {
        if (game.IsEditing)
            return;

        EditingOriginalName = game.Name;
        game.IsEditing = true;
    }

    public void CommitRename(Game game, string? newName)
    {
        game.IsEditing = false;

        if (string.IsNullOrWhiteSpace(newName) || newName == EditingOriginalName)
        {
            if (EditingOriginalName != null)
                game.Name = EditingOriginalName;
            EditingOriginalName = null;
            return;
        }

        game.Name = newName;
        EditingOriginalName = null;
    }

    public void CancelRename(Game game)
    {
        game.IsEditing = false;
        if (EditingOriginalName != null)
        {
            game.Name = EditingOriginalName;
            EditingOriginalName = null;
        }
    }

    public List<string> GetSelectedAppIds()
    {
        return Games
            .Where(g => g.IsEditing == false)
            .Select(g => g.AppId)
            .ToList();
    }

    public async Task ImportAppIdsAsync(IEnumerable<string> appIds, Profile? profile)
    {
        if (profile == null) return;

        var appIdsList = appIds.ToList();
        Logger.Info($"ImportAppIdsAsync: starting with {appIdsList.Count} app IDs for profile '{profile.Name}'");

        var semaphore = new SemaphoreSlim(6);
        var tasks = new List<Task>();
        var importedGames = new ConcurrentBag<Game>();
        var resolvedCount = 0;
        var skippedCount = 0;

        foreach (var id in appIdsList)
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

                        if (info.DlcDepots.TryGetValue(id, out var dlcDepots))
                            depotsToAssign = dlcDepots;
                        else if (info.Depots.Count > 0)
                            depotsToAssign = info.Depots;

                        if (depotsToAssign != null)
                            game.Depots = depotsToAssign
                                .Where(depotId => appIdsList.Contains(depotId))
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
                    Logger.Debug($"App {id}: imported as '{game.Name}' (type={game.Type})");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"App {id}: unexpected error during import: {ex.Message}");
                }
                finally
                {
                    if (resolved) Interlocked.Increment(ref resolvedCount);
                    else Interlocked.Increment(ref skippedCount);
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        Logger.Info($"ImportAppIdsAsync: {resolvedCount} resolved, {skippedCount} skipped (of {appIdsList.Count} total)");

        var addedCount = 0;
        foreach (var game in importedGames.OrderBy(g => int.Parse(g.AppId)))
        {
            if (!Games.Any(g => g.AppId == game.AppId))
            {
                Games.Add(game);
                profile.Games.Add(game);
                addedCount++;
            }
        }

        Logger.Info($"ImportAppIdsAsync: added {addedCount} new games to profile, profile now has {profile.Games.Count} total, UI shows {Games.Count}");

        _notificationManager.UpdateGameCount(Games.Count);
        UpdateGameListState();
    }

    public int GetUncheckedGameCount()
    {
        return Games.Count(g => g.IsEditing);
    }
}
