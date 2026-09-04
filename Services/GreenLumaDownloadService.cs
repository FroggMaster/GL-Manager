using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GreenLuma_Manager.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GreenLuma_Manager.Services;

public static partial class GreenLumaDownloadService
{
    private const int PollIntervalMs = 1500;
    private const int PageTimeoutMs = 30000;
    private const int DownloadTimeoutMs = 120000;

    private const string LoginUrl = "https://cs.rin.ru/forum/ucp.php?mode=login";
    private const string ForumUrl = "https://cs.rin.ru/forum/viewtopic.php?f=29&t=103709";

    /// <summary>
    /// Downloads the latest GreenLuma ZIP from cs.rin.ru.
    /// Uses WebView2 to handle the nginx JS security challenge and authenticate,
    /// then extracts the attachment download link and saves the file.
    /// </summary>
    /// <param name="username">cs.rin.ru username.</param>
    /// <param name="password">cs.rin.ru password.</param>
    /// <param name="downloadFolder">Folder to save the downloaded file.</param>
    /// <param name="config">Application configuration.</param>
    /// <param name="statusCallback">Optional callback for status text updates.</param>
    /// <param name="progressCallback">Optional callback for progress (0.0 to 1.0).</param>
    public static async Task<GreenLumaDownloadResult> DownloadLatestGreenLumaAsync(
        string username,
        string password,
        string downloadFolder,
        Config config,
        Action<string>? statusCallback = null,
        IProgress<double>? progressCallback = null)
    {
        var result = new GreenLumaDownloadResult();
        var diagnostics = new List<string>();

        var dispatcherOp = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (Application.Current.MainWindow is not MainWindow mainWindow)
            {
                result.ErrorMessage = "MainWindow not available";
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

                webView.CoreWebView2.Settings.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

                // ── Step 1: Log in ────────────────────────────────────
                statusCallback?.Invoke("Logging in to cs.rin.ru...");
                var (loginOk, loginDiag) = await LoginAsync(webView, username, password);
                diagnostics.AddRange(loginDiag);
                if (!loginOk)
                {
                    result.ErrorMessage = "Failed to log in to cs.rin.ru.\n" + string.Join("\n", diagnostics.TakeLast(3));
                    return;
                }
                progressCallback?.Report(0.15);

                // ── Step 2: Find attachment download URL ──────────────
                statusCallback?.Invoke("Finding download link...");
                var (clickLinkJs, findDiag) = await FindDownloadLinkJsAsync(webView);
                diagnostics.AddRange(findDiag);
                if (string.IsNullOrWhiteSpace(clickLinkJs))
                {
                    result.ErrorMessage = "Could not find GreenLuma download link.\n" + string.Join("\n", diagnostics.TakeLast(5));
                    return;
                }
                progressCallback?.Report(0.30);

                // ── Step 3: Download the ZIP ──────────────────────────
                statusCallback?.Invoke("Downloading GreenLuma...");

                // Map download file progress (0.0-1.0) to the range 0.30-1.0 of this service
                var mappedProgress = progressCallback != null
                    ? new Progress<double>(pct => progressCallback.Report(0.30 + pct * 0.70))
                    : null;

                var downloadResult = await DownloadFileAsync(webView, clickLinkJs, downloadFolder, statusCallback, mappedProgress);
                if (!downloadResult.Success)
                {
                    result.ErrorMessage = downloadResult.ErrorMessage ?? "Download failed";
                    return;
                }

                result.Success = true;
                result.FilePath = downloadResult.FilePath;
                result.FileName = Path.GetFileName(downloadResult.FilePath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error downloading GreenLuma");
                var diag = string.Join("\n", diagnostics.TakeLast(5));
                result.ErrorMessage = diag.Length > 0 ? $"{ex.Message}\n{diag}" : ex.Message;
            }
            finally
            {
                hostPanel.Children.Remove(webView);
                webView.Dispose();
            }
        }, DispatcherPriority.Normal);

        // Dispatcher.InvokeAsync(Func<Task>) returns DispatcherOperation<Task>.
        // await on DispatcherOperation<Task> returns the Task from the async lambda.
        // We must await that Task too, otherwise DownloadLatestGreenLumaAsync returns
        // the default result before the lambda has done any work.
        var innerTask = await dispatcherOp;
        await innerTask;

        return result;
    }

    // ── Login ────────────────────────────────────────────────────────

    private static async Task<(bool Ok, List<string> Diagnostics)> LoginAsync(
        WebView2 webView, string username, string password)
    {
        var diag = new List<string>();

        // Clear all cookies so new credentials are always used.
        // Without this, the server redirects away from the login page
        // (WebView2 reuses the cached session) and LoginAsync returns
        // early without ever attempting authentication.
        webView.CoreWebView2.CookieManager.DeleteAllCookies();
        Logger.Debug("Navigate to login page (cookies cleared)");
        diag.Add("Navigate to login page (cookies cleared)");
        webView.CoreWebView2.Navigate(LoginUrl);

        // Wait for the page to settle (handles JS challenge delay + redirect).
        // Use a generic body-length check first — the page might redirect to index
        // if the WebView2 already has a session cookie from a previous run.
        if (!await PollForConditionAsync(webView,
                "document.body && document.body.innerText.length > 50", PageTimeoutMs))
        {
            diag.Add("Timed out waiting for page to load");
            return (false, diag);
        }

        // Check if we were redirected to the index page (already authenticated)
        var currentUrl = await webView.CoreWebView2.ExecuteScriptAsync("window.location.href");
        var url = currentUrl?.Trim('"') ?? "";
        diag.Add($"Post-navigate URL: {url}");

        if (!url.Contains("ucp.php?mode=login") && !url.Contains("ucp.php"))
        {
            Logger.Debug("Already logged in (redirected from login page)");
            diag.Add("Already logged in, skipping form fill");
            return (true, diag);
        }

        // Log what we found on the page for diagnostics
        var pageInfo = await webView.CoreWebView2.ExecuteScriptAsync(@"
            JSON.stringify({
                userField: document.querySelector('#username') !== null ? '#username' :
                           document.querySelector('input[name=""username""]') !== null ? 'input[name=username]' :
                           document.querySelector('input[type=""text""]') !== null ? 'input[type=text]' : 'none',
                passField: document.querySelector('#password') !== null ? '#password' :
                           document.querySelector('input[name=""password""]') !== null ? 'input[name=password]' :
                           document.querySelector('input[type=""password""]') !== null ? 'input[type=password]' : 'none',
                submitBtn: document.querySelector('input[type=""submit""]') !== null ? 'input[type=submit]' :
                           document.querySelector('button[type=""submit""]') !== null ? 'button[type=submit]' : 'none',
                url: window.location.href.substring(0, 100),
                title: document.title.substring(0, 80)
            })
        ");
        diag.Add($"Page state: {pageInfo}");

        // Build fill-and-submit JS — uses whichever selector matched
        var fillJs = @"
            (function() {
                var userField = document.querySelector('#username') ||
                               document.querySelector('input[name=""username""]') ||
                               document.querySelector('input[type=""text""]');
                var passField = document.querySelector('#password') ||
                               document.querySelector('input[name=""password""]');
                if (!passField && userField) {
                    passField = userField.parentElement.querySelector('input[type=""password""]');
                }
                if (!userField || !passField) return 'FIELDS_NOT_FOUND';
                userField.value = '" + EscapeJs(username) + @"';
                passField.value = '" + EscapeJs(password) + @"';
                var auto = document.getElementById('autologin');
                if (auto) auto.checked = true;
                var btn = document.querySelector('input[type=""submit""]') ||
                          document.querySelector('button[type=""submit""]');
                if (btn) { btn.click(); return 'CLICKED'; }
                var form = userField.closest('form');
                if (form) { form.submit(); return 'FORM_SUBMIT'; }
                return 'NO_SUBMIT_BUTTON';
            })()
        ";

        var jsResult = await webView.CoreWebView2.ExecuteScriptAsync(fillJs);
        var submitMethod = jsResult?.Trim('"');
        diag.Add($"Submit method: {submitMethod}");

        if (submitMethod == "FIELDS_NOT_FOUND" || submitMethod == "NO_SUBMIT_BUTTON")
        {
            diag.Add($"Could not fill/submit login form (result: {submitMethod})");
            return (false, diag);
        }

        Logger.Debug($"Login submitted ({submitMethod}), waiting for redirect...");

        // Wait for redirect away from login page
        if (!await PollForConditionAsync(webView,
                "!window.location.href.includes('ucp.php?mode=login')", PageTimeoutMs))
        {
            // Check for error message on the login page
            var errorCheck = await webView.CoreWebView2.ExecuteScriptAsync(@"
                (function() {
                    var err = document.querySelector('.error') ||
                              document.querySelector('.notification') ||
                              document.querySelector('[class*=""error""]');
                    return err ? err.innerText.substring(0, 200) : 'no error element found';
                })()
            ");
            diag.Add($"Login failed, page error: {errorCheck?.Trim('"')}");
            return (false, diag);
        }

        Logger.Debug("Login successful");
        diag.Add("Login successful");
        return (true, diag);
    }

    // ── Find download link and return JS to click it ────────────────

    private static async Task<(string? ClickJs, List<string> Diagnostics)> FindDownloadLinkJsAsync(WebView2 webView)
    {
        var diag = new List<string>();

        Logger.Debug("Navigating to forum topic...");
        diag.Add("Navigate to forum topic");
        webView.CoreWebView2.Navigate(ForumUrl);

        // Wait for body to have content
        if (!await PollForConditionAsync(webView,
                "document.body && document.body.innerText.length > 200", PageTimeoutMs))
        {
            diag.Add("Timed out waiting for topic page to load");
            return (null, diag);
        }

        // Log a snippet of the page for debugging
        var snippet = await webView.CoreWebView2.ExecuteScriptAsync(
            "document.body.innerText.substring(0, 300)");
        diag.Add($"Page snippet: {snippet?.Trim('"')?.Replace('\n', ' ')}");

        // Poll for attachment download links
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < PageTimeoutMs)
        {
            await Task.Delay(PollIntervalMs);

            var linksJson = await webView.CoreWebView2.ExecuteScriptAsync(@"
                (function() {
                    var all = document.querySelectorAll('a[href*=""download/file.php""]');
                    if (all.length === 0) return '';
                    // Look for a ZIP containing GreenLuma
                    for (var i = 0; i < all.length; i++) {
                        var text = (all[i].textContent || all[i].innerText || '').trim();
                        if (text.includes('.zip') && text.includes('GreenLuma')) {
                            return all[i].href;
                        }
                    }
                    // Fallback: any .zip link
                    for (var i = 0; i < all.length; i++) {
                        var text = (all[i].textContent || all[i].innerText || '').trim();
                        if (text.includes('.zip')) {
                            return all[i].href;
                        }
                    }
                    // Return first link
                    return all[0].href;
                })()
            ");

            var downloadUrl = linksJson?.Trim('"');
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                if (stopwatch.ElapsedMilliseconds > 5000)
                {
                    // Check if we're actually logged in
                    var loginCheck = await webView.CoreWebView2.ExecuteScriptAsync(
                        "document.body.innerText.includes('Logout') || document.body.innerText.includes('log out')");
                    diag.Add($"Login check (text contains 'Logout'): {loginCheck?.Trim('"')}");
                }
                continue;
            }

            Logger.Debug($"Found link: {downloadUrl}");
            diag.Add($"Download link: {downloadUrl}");

            // Return JS to click the link on the page (preserves referrer/cookies)
            var clickJs = $@"
                (function() {{
                    var links = document.querySelectorAll('a[href*=""download/file.php""]');
                    for (var i = 0; i < links.length; i++) {{
                        if (links[i].href === '{EscapeJs(downloadUrl)}') {{
                            links[i].click();
                            return 'CLICKED';
                        }}
                    }}
                    return 'LINK_NOT_FOUND';
                }})()
            ";
            return (clickJs, diag);
        }

        diag.Add("Timed out searching for download links");
        return (null, diag);
    }

    // ── Download ─────────────────────────────────────────────────────

    private static async Task<GreenLumaDownloadResult> DownloadFileAsync(
        WebView2 webView, string clickLinkJs, string downloadFolder,
        Action<string>? statusCallback = null,
        IProgress<double>? progressCallback = null)
    {
        var downloadTcs = new TaskCompletionSource<GreenLumaDownloadResult>();
        var lastReportedProgress = 0.0;

        webView.CoreWebView2.DownloadStarting += (sender, args) =>
        {
            // WebView2 appends " (1)" to the filename via built-in dedup before firing this event.
            // Since this is a temp download, strip any WebView2 dedup suffix and overwrite.
            var rawName = Path.GetFileName(args.DownloadOperation.ResultFilePath);
            var cleanName = StripWebView2Dedup(rawName);
            if (string.IsNullOrWhiteSpace(cleanName))
                cleanName = "GreenLuma_Latest.zip";

            var fullPath = Path.Combine(downloadFolder, cleanName);

            // Overwrite any existing file — this is a TMP download
            if (File.Exists(fullPath))
                File.Delete(fullPath);

            args.ResultFilePath = fullPath;
            args.Handled = true;

            Logger.Debug($"Downloading to: {fullPath}");

            args.DownloadOperation.StateChanged += (s, e) =>
            {
                var op = args.DownloadOperation;

                // Report download progress when bytes info is available
                if (op.TotalBytesToReceive > 0)
                {
                    var pct = Math.Min(op.BytesReceived / (double)op.TotalBytesToReceive, 1.0);
                    if (pct - lastReportedProgress >= 0.01 || pct >= 1.0)
                    {
                        lastReportedProgress = pct;
                        progressCallback?.Report(pct);
                        statusCallback?.Invoke($"Downloading GreenLuma... {pct * 100:F0}%");
                    }
                }

                switch (op.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        progressCallback?.Report(1.0);
                        statusCallback?.Invoke("Download complete");
                        Logger.Debug("Download completed");
                        downloadTcs.TrySetResult(new GreenLumaDownloadResult
                        {
                            Success = true,
                            FilePath = fullPath,
                            FileName = cleanName
                        });
                        break;

                    case CoreWebView2DownloadState.Interrupted:
                        var reason = op.InterruptReason.ToString();
                        Logger.Debug($"Download interrupted: {reason}");
                        downloadTcs.TrySetResult(new GreenLumaDownloadResult
                        {
                            Success = false,
                            ErrorMessage = $"Download interrupted: {reason}"
                        });
                        break;
                }
            };
        };

        // Click the download link on the page using JavaScript
        // This preserves referrer and cookies better than Navigate
        Logger.Debug("Clicking download link...");
        var clickResult = await webView.CoreWebView2.ExecuteScriptAsync(clickLinkJs);
        Logger.Debug($"Click result: {clickResult}");

        // Wait for download to complete (with extended timeout)
        var completedTask = await Task.WhenAny(downloadTcs.Task, Task.Delay(DownloadTimeoutMs));
        if (completedTask != downloadTcs.Task)
        {
            Logger.Debug("Download timed out");
            return new GreenLumaDownloadResult
            {
                Success = false,
                ErrorMessage = "Download timed out after 2 minutes"
            };
        }

        return await downloadTcs.Task;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task<bool> PollForConditionAsync(WebView2 webView, string jsCondition, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();

        // Initial delay to let navigation start
        await Task.Delay(1000);

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var resultJson = await webView.CoreWebView2.ExecuteScriptAsync(jsCondition);
                var result = resultJson?.Trim('"');

                if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // Page may still be navigating
            }

            await Task.Delay(PollIntervalMs);
        }

        return false;
    }

    private static string EscapeJs(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    /// <summary>
    /// Strips WebView2's automatic "(1)", "(2)" etc. dedup suffix from a filename.
    /// WebView2 appends these before DownloadStarting fires if the file already exists.
    /// </summary>
    private static string StripWebView2Dedup(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return fileName;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        // Match " (1)", " (2)" etc. at end of name
        var trimmed = System.Text.RegularExpressions.Regex.Replace(
            nameWithoutExt, @"\s+\(\d+\)$", "");

        return trimmed + ext;
    }
}
