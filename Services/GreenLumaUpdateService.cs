using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using GreenLuma_Manager.Models;
using Microsoft.Web.WebView2.Wpf;

namespace GreenLuma_Manager.Services;

public static partial class GreenLumaUpdateService
{
    private const int PollIntervalMs = 1500;
    private const int FetchTimeoutMs = 30000;

    [GeneratedRegex(@"GreenLuma \d{4}\s+(\d+\.\d+\.\d+)")]
    private static partial Regex VersionTitleRegex();

    public static async Task<GreenLumaVersionInfo?> CheckForGreenLumaUpdatesAsync(Config config)
    {
        if (!config.CheckGreenLumaUpdates)
            return null;

        try
        {
            var title = await FetchForumPageTitleAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(title))
                return CreateFailedResult("Failed to retrieve forum page title");

            var semanticVersion = ParseVersionFromTitle(title);
            if (semanticVersion == null)
                return CreateFailedResult($"Failed to parse version from title: {title}");

            var (installedVersion, _) = GreenLumaService.DetectInstalledVersion(config.GreenLumaPath);

            return new GreenLumaVersionInfo
            {
                LatestVersionTag = title,
                LatestSemanticVersion = semanticVersion,
                InstalledVersion = installedVersion,
                CheckedAt = DateTime.UtcNow,
                CheckSucceeded = true,
                ErrorMessage = null
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error checking for updates");
            return CreateFailedResult(ex.Message);
        }
    }

    /// <summary>
    /// Fetches the forum page title using an invisible WebView2.
    /// Handles the nginx JS security challenge by polling <c>document.title</c>
    /// until it matches the expected GreenLuma version pattern (security
    /// redirect is async). The WebView2 is hosted in the MainWindow's visual
    /// tree because CoreWebView2 requires a parent container to function.
    /// </summary>
    private static async Task<string?> FetchForumPageTitleAsync()
    {
        var tcs = new TaskCompletionSource<string?>();

        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            // WebView2 must be in the visual tree with non-zero size for
            // CoreWebView2 to render pages and execute JavaScript.
            if (Application.Current.MainWindow is not MainWindow mainWindow)
            {
                tcs.TrySetResult(null);
                return;
            }

            var hostPanel = mainWindow.PnlWebView2Host;
            var webView = new WebView2
            {
                Width = 1,
                Height = 1,
                Visibility = Visibility.Visible
            };

            hostPanel.Children.Add(webView);

            try
            {
                var environment = await WebView2Helper.GetEnvironmentAsync();
                await webView.EnsureCoreWebView2Async(environment);

                // Spoof a Chrome User-Agent so the nginx security module
                // doesn't treat WebView2 differently from a real browser.
                webView.CoreWebView2.Settings.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

                webView.CoreWebView2.Navigate(GreenLumaVersionInfo.ForumUrl);

                // Poll document.title until it matches the version pattern.
                // The cs.rin.ru nginx JS challenge loads a security page first,
                // then redirects to the real topic after the challenge passes.
                // Relying on NavigationCompleted alone is unreliable because
                // it fires for the challenge page before the redirect.
                var stopwatch = Stopwatch.StartNew();
                string? lastTitle = null;

                while (stopwatch.ElapsedMilliseconds < FetchTimeoutMs)
                {
                    await Task.Delay(PollIntervalMs);

                    try
                    {
                        var titleJson = await webView.CoreWebView2
                            .ExecuteScriptAsync("document.title");

                        var title = titleJson?.Trim('"');
                        if (string.IsNullOrWhiteSpace(title))
                            continue;

                        lastTitle = title;

                        if (VersionTitleRegex().IsMatch(title))
                        {
                            tcs.TrySetResult(title);
                            return;
                        }
                    }
                    catch
                    {
                        // Page may still be navigating — try again next poll
                    }
                }

                // Timeout reached
                Logger.Debug(lastTitle != null
                    ? $"Timeout fetching forum page title (last title: {lastTitle})"
                    : "Timeout fetching forum page title");
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "WebView2 initialization error");
                tcs.TrySetResult(null);
            }
            finally
            {
                hostPanel.Children.Remove(webView);
                webView.Dispose();
            }
        }, DispatcherPriority.Normal);

        return await tcs.Task.ConfigureAwait(false);
    }

    private static Version? ParseVersionFromTitle(string title)
    {
        var match = VersionTitleRegex().Match(title);
        if (!match.Success)
            return null;

        if (!Version.TryParse(match.Groups[1].Value, out var semanticVersion))
            return null;

        return semanticVersion;
    }

    private static GreenLumaVersionInfo CreateFailedResult(string errorMessage)
    {
        return new GreenLumaVersionInfo
        {
            LatestVersionTag = string.Empty,
            LatestSemanticVersion = null,
            InstalledVersion = null,
            CheckedAt = DateTime.UtcNow,
            CheckSucceeded = false,
            ErrorMessage = errorMessage
        };
    }
}