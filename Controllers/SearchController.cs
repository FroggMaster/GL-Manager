using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager.Controllers;

public class SearchController
{
    private readonly DataGrid _dgResults;
    private readonly UIElement _pnlSearchLoading;
    private readonly TextBlock _txtResultCount;
    private readonly UIElement _pnlEmptyResults;
    private readonly TextBlock _txtLoadingDots;
    private readonly NotificationManager _notificationManager;

    private readonly ObservableCollection<Game> _searchResults;
    private CancellationTokenSource? _searchCts;
    private string? _sortMemberPath;
    private bool _sortAscending = true;
    private bool _rebuilding;

    /// <summary>Returns whether an app id is already in the active profile (drives the results grid buttons).</summary>
    public Func<string, bool>? IsAppIdInProfile { get; set; }

    public event Action<Game>? GameSelected;

    public ObservableCollection<Game> SearchResults => _searchResults;

    public SearchController(
        DataGrid dgResults,
        UIElement pnlSearchLoading,
        TextBlock txtResultCount,
        UIElement pnlEmptyResults,
        TextBlock txtLoadingDots,
        NotificationManager notificationManager,
        ObservableCollection<Game>? searchResults = null)
    {
        _dgResults = dgResults;
        _pnlSearchLoading = pnlSearchLoading;
        _txtResultCount = txtResultCount;
        _pnlEmptyResults = pnlEmptyResults;
        _txtLoadingDots = txtLoadingDots;
        _notificationManager = notificationManager;

        _searchResults = searchResults ?? [];
        _dgResults.ItemsSource = _searchResults;
    }

    public async Task ExecuteSearchAsync(string query)
    {
        Logger.Info($"ExecuteSearchAsync: query='{query}'");

        if (string.IsNullOrWhiteSpace(query))
        {
            Logger.Warn("ExecuteSearchAsync: empty query");
            _notificationManager.ShowToast("Enter a search term", false);
            return;
        }

        if (query.Length < 3)
        {
            Logger.Warn($"ExecuteSearchAsync: query too short ({query.Length} chars)");
            _notificationManager.ShowToast("Search term must be at least 3 characters", false);
            return;
        }

        if (query.Length > 200)
        {
            Logger.Warn($"ExecuteSearchAsync: query too long ({query.Length} chars)");
            _notificationManager.ShowToast("Search term is too long (max 200 characters)", false);
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            await PerformSearchAsync(query, token);
        }
        catch (OperationCanceledException)
        {
            Logger.Info("ExecuteSearchAsync: search cancelled");
            _notificationManager.StopLoadingDots();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "ExecuteSearchAsync: search failed");
            _notificationManager.StopLoadingDots();
            _notificationManager.ShowToast("Search failed: " + ex.Message, false);
        }
    }

    private async Task PerformSearchAsync(string query, CancellationToken token)
    {
        Logger.Debug($"PerformSearchAsync: starting search for query='{query}'");
        Keyboard.ClearFocus();
        ShowSearchLoading();

        var results = await Task.Run(() => SearchService.SearchAsync(query), token);

        if (token.IsCancellationRequested)
            return;

        Logger.Debug($"PerformSearchAsync: search completed, {results.Count} results");
        DisplaySearchResults(results, token);
    }

    private void ShowSearchLoading()
    {
        _dgResults.Visibility = Visibility.Collapsed;
        _pnlEmptyResults.Visibility = Visibility.Collapsed;
        _pnlSearchLoading.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var scaleIn = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        _pnlSearchLoading.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        if (_pnlSearchLoading.RenderTransform is ScaleTransform scaleTransform)
        {
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
        }

        _notificationManager.StartLoadingDots();
    }

    private void HideSearchLoading()
    {
        _notificationManager.StopLoadingDots();
        _pnlSearchLoading.Visibility = Visibility.Collapsed;
    }

    private void DisplaySearchResults(List<Game> results, CancellationToken token)
    {
        HideSearchLoading();

        _searchResults.Clear();

        if (results.Count == 0)
        {
            _dgResults.Visibility = Visibility.Collapsed;
            _pnlEmptyResults.Visibility = Visibility.Visible;
            return;
        }

        foreach (var game in results.Take(60))
        {
            game.IsExpanded = false;
            game.IsInProfile = IsAppIdInProfile?.Invoke(game.AppId) ?? false;
            foreach (var dlc in game.Dlcs)
                dlc.IsInProfile = IsAppIdInProfile?.Invoke(dlc.AppId) ?? false;
            _searchResults.Add(game);
        }

        Logger.Debug($"DisplaySearchResults: bound {_searchResults.Count} results");

        _dgResults.Visibility = Visibility.Visible;
        _txtResultCount.Text = results.Count > 60
            ? $"Showing 60 of {results.Count} results"
            : $"{results.Count} results";

        _ = FetchIconsInBackgroundAsync(results, token);
    }

    private async Task FetchIconsInBackgroundAsync(List<Game> results, CancellationToken token)
    {
        try
        {
            // Include the nested DLCs so their icons resolve as well.
            var all = results.Concat(results.SelectMany(g => g.Dlcs)).ToList();
            await SearchService.FetchIconUrlsAsync(all).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public void CancelSearch()
    {
        Logger.Info("CancelSearch: cancelling search");
        _searchCts?.Cancel();
    }

    /// <summary>
    /// Inserts or removes a game's DLC rows directly beneath it in the results when its expander
    /// is toggled. The collection is manipulated (rather than binding row visibility) because the
    /// DataGrid reliably reflects collection changes.
    /// </summary>
    public void SetDlcExpanded(Game game, bool expanded)
    {
        if (_rebuilding || game is not { Dlcs.Count: > 0 })
            return;

        if (expanded)
        {
            var insertAt = _searchResults.IndexOf(game) + 1;
            if (insertAt <= 0)
                return;

            foreach (var dlc in game.Dlcs)
            {
                dlc.Parent = game;
                if (_searchResults.Contains(dlc))
                    continue;

                _searchResults.Insert(insertAt, dlc);
                insertAt++;
            }
        }
        else
        {
            foreach (var dlc in game.Dlcs)
                _searchResults.Remove(dlc);
        }
    }

    /// <summary>
    /// Sorts the results by the given member, toggling direction when the same column is clicked
    /// again, and returns the resulting direction. Only base games are sorted; each game's DLC rows
    /// stay directly beneath it so nesting is never interrupted.
    /// </summary>
    public bool ApplySort(string sortMemberPath)
    {
        if (string.Equals(_sortMemberPath, sortMemberPath, StringComparison.Ordinal))
            _sortAscending = !_sortAscending;
        else
        {
            _sortMemberPath = sortMemberPath;
            _sortAscending = true;
        }

        RebuildSorted();
        return _sortAscending;
    }

    private void RebuildSorted()
    {
        if (_searchResults.Count == 0)
            return;

        // Remember which parents currently have their DLC rows expanded.
        var expandedParents = new HashSet<Game>();
        foreach (var game in _searchResults)
            if (game.Parent != null)
                expandedParents.Add(game.Parent);

        var baseGames = _searchResults.Where(g => g.Parent == null).ToList();
        var path = _sortMemberPath ?? "Name";

        var ordered = _sortAscending
            ? baseGames.OrderBy(g => SortKey(g, path), StringComparer.OrdinalIgnoreCase)
            : baseGames.OrderByDescending(g => SortKey(g, path), StringComparer.OrdinalIgnoreCase);

        _rebuilding = true;
        try
        {
            _searchResults.Clear();
            foreach (var game in ordered)
            {
                _searchResults.Add(game);

                if (!expandedParents.Contains(game))
                    continue;

                foreach (var dlc in game.Dlcs)
                {
                    dlc.Parent = game;
                    _searchResults.Add(dlc);
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private static string SortKey(Game game, string sortMemberPath) => sortMemberPath switch
    {
        "AppId" => game.AppId.PadLeft(12, '0'),
        "Type" => game.Type,
        _ => game.Name
    };

    public void OnSearchResultDoubleClick(Game game)
    {
        GameSelected?.Invoke(game);
    }

    /// <summary>Re-evaluates the "already in profile" state of the displayed results and their DLCs.</summary>
    public void RefreshProfileFlags()
    {
        if (IsAppIdInProfile == null)
            return;

        foreach (var game in _searchResults)
        {
            game.IsInProfile = IsAppIdInProfile(game.AppId);
            foreach (var dlc in game.Dlcs)
                dlc.IsInProfile = IsAppIdInProfile(dlc.AppId);
        }
    }
}
