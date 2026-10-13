using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Controllers;

public class GameListController
{
    private readonly ItemsControl _lstGames;
    private readonly TextBlock _txtGameCount;
    private readonly UIElement _pnlEmptyGames;
    private readonly NotificationManager _notificationManager;
    private readonly Dispatcher _dispatcher;
    private ICollectionView? _gamesView;
    private string? _searchFilter;
    private string? _typeFilter;
    private DispatcherTimer? _highlightTimer;
    private Game? _highlightedGame;

    private static readonly TimeSpan HighlightDuration = TimeSpan.FromMilliseconds(1500);

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
        _dispatcher = lstGames.Dispatcher;

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
        var showDlcsFlat = hasType && string.Equals(_typeFilter, "DLC", StringComparison.OrdinalIgnoreCase);

        var rawFilter = _searchFilter?.Trim();
        var normalizedFilter = !string.IsNullOrWhiteSpace(rawFilter)
            ? NormalizeForSearch(rawFilter)
            : null;

        var typeFilterValue = _typeFilter;

        _gamesView!.Filter = obj =>
        {
            if (obj is not Game game) return false;

            // DLCs are shown nested under their parent game rather than as top-level entries,
            // unless the user explicitly filters for DLCs.
            var isDlc = !string.IsNullOrEmpty(game.ParentAppId);
            if (isDlc != showDlcsFlat)
                return false;

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

        _gamesView!.Refresh();

        if (!IsFilterActive)
        {
            _notificationManager.UpdateGameCount(Games.Count);
        }
        else
        {
            var filteredCount = 0;
            foreach (var _ in _gamesView!)
                filteredCount++;
            _notificationManager.UpdateGameCount(filteredCount, true);
        }

        UpdateGameListState();
    }

    /// <summary>
    /// Rebuilds DLC links and re-applies the view filter after DLC parent links change, so nested
    /// DLCs and virtual parent headers appear/disappear correctly.
    /// </summary>
    public void RefreshDlcGrouping()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(RefreshDlcGrouping);
            return;
        }

        PopulateDlcLinks();
        ApplyFilters();
    }

    /// <summary>
    /// Links each profile game's DLC entries to it so they can be nested under it in the list.
    /// A DLC's <see cref="Game.ParentAppId"/> points at its base game's app id.
    /// </summary>
    public void PopulateDlcLinks()
    {
        var dlcsByParent = Games
            .Where(g => !string.IsNullOrEmpty(g.ParentAppId))
            .GroupBy(g => g.ParentAppId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var game in Games)
        {
            game.Dlcs.Clear();
            if (dlcsByParent.TryGetValue(game.AppId, out var children))
                foreach (var child in children)
                    game.Dlcs.Add(child);

            game.NotifyDlcIndicatorsChanged();
        }
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
        ClearActiveHighlight();
        Games.Clear();
        PopulateDlcLinks();
        _notificationManager.UpdateGameCount(Games.Count);
        UpdateGameListState();
    }

    public void AddGame(Game game) => AddGames([game]);

    /// <summary>
    /// Adds a batch of games, skipping duplicates and keeping the list alphabetically ordered.
    /// </summary>
    public IReadOnlyList<Game> AddGames(IReadOnlyCollection<Game> games)
    {
        var added = new List<Game>();

        foreach (var game in games)
        {
            if (Games.Any(g => g.AppId == game.AppId))
                continue;

            // Insert in alphabetical order by name
            var index = 0;
            while (index < Games.Count && string.Compare(Games[index].Name, game.Name, StringComparison.OrdinalIgnoreCase) <= 0)
                index++;

            Games.Insert(index, game);
            added.Add(game);
        }

        if (added.Count == 0)
            return [];

        PopulateDlcLinks();
        _notificationManager.UpdateGameCount(Games.Count);
        UpdateGameListState();
        return added;
    }

    public void RemoveGame(Game game)
    {
        // If the game still has DLCs in the profile, keep it as a virtual parent ("base game not in
        // profile") so the installed DLCs remain visible; otherwise remove it.
        if (Games.Any(g => string.Equals(g.ParentAppId, game.AppId, StringComparison.Ordinal)))
            game.IsVirtual = true;
        else
            Games.Remove(game);

        // Drop virtual parents that no longer have any DLCs in the profile.
        foreach (var virtualParent in Games.Where(g => g.IsVirtual &&
                     !Games.Any(d => string.Equals(d.ParentAppId, g.AppId, StringComparison.Ordinal))).ToList())
        {
            Games.Remove(virtualParent);
        }

        PopulateDlcLinks();
        _notificationManager.UpdateGameCount(Games.Count);
        UpdateGameListState();
    }

    public void LoadGames(IEnumerable<Game> games)
    {
        ClearActiveHighlight();
        Games.Clear();
        foreach (var game in games)
        {
            game.IsHighlighted = false;
            Games.Add(game);
        }

        // Clear search filter when loading a new set of games
        _searchFilter = null;
        PopulateDlcLinks();
        ApplyFilters();
    }

    /// <summary>
    /// Highlights the game explicitly added from search results and scrolls the list to reveal it.
    /// Any previous add highlight is cleared immediately.
    /// </summary>
    public void HighlightSearchAddedGame(Game game)
    {
        ClearActiveHighlight();

        _highlightedGame = game;
        game.IsHighlighted = true;

        _dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _lstGames.UpdateLayout();
                SmoothScroll.BringIntoView(
                    _lstGames.ItemContainerGenerator.ContainerFromItem(game) as FrameworkElement);
            }));

        var timer = new DispatcherTimer { Interval = HighlightDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_highlightedGame, game))
                return;

            game.IsHighlighted = false;
            _highlightedGame = null;
            _highlightTimer = null;
        };
        _highlightTimer = timer;
        timer.Start();
    }

    private void ClearActiveHighlight()
    {
        _highlightTimer?.Stop();
        _highlightTimer = null;

        if (_highlightedGame != null)
            _highlightedGame.IsHighlighted = false;
        _highlightedGame = null;
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
        foreach (var game in importedGames)
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
