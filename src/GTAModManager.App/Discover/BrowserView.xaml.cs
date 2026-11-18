using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GTAModManager.App.Discover;

/// <summary>
/// Embedded browser (WebView2) for mod pages. Created on first use; browser data lives in the app data
/// folder. Only https URLs are allowed; new windows open in place. Without the WebView2 runtime
/// OpenAsync returns false and the link is opened in the system browser.
/// </summary>
public partial class BrowserView : UserControl, IDisposable
{
    private WebView2? _web;
    private Task<bool>? _init;
    private bool _disposed;
    private readonly Dictionary<string, CoreWebView2DownloadOperation> _downloads = [];

    public void CancelDownload(string id)
    {
        if (_downloads.TryGetValue(id, out var operation))
            operation.Cancel();
    }

    public event Action? CloseRequested;
    public string? DownloadGameRoot { get; set; }
    public string? OriginUrl { get; set; }
    public event Action<DownloadReceipt>? DownloadStarted;
    public event Action<DownloadReceipt>? DownloadFailed;
    public event Action<DownloadReceipt>? DownloadCompleted;

    public BrowserView() => InitializeComponent();

    public static string UserDataDir => GTAModManager.Core.AppPaths.Browser;

    public async Task<bool> OpenAsync(Uri uri)
    {
        if (_disposed || !LinkSafety.IsSafeWebUrl(uri.AbsoluteUri))
            return false;
        // Wait for layout: WebView2 gets a window handle only once the control is on screen.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        var init = _init ??= InitAsync();
        if (await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(20))) != init)
            return false; // gave up waiting: use the system browser
        if (!await init)
        {
            _init = null;
            return false;
        }
        AddressText.Text = uri.AbsoluteUri;
        _web!.CoreWebView2.Navigate(uri.AbsoluteUri);
        return true;
    }

    private async Task<bool> InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataDir);
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 11, 11) };
            WebHost.Children.Add(_web);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = true;
            core.Settings.IsStatusBarEnabled = true;
            core.NavigationStarting += (_, e) =>
            {
                // https only: no file:, javascript: or third-party app protocols.
                if (!LinkSafety.IsSafeWebUrl(e.Uri))
                    e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (LinkSafety.IsSafeWebUrl(e.Uri))
                    core.Navigate(e.Uri);
            };
            core.DownloadStarting += OnDownloadStarting;
            core.HistoryChanged += (_, _) => UpdateNav();
            core.SourceChanged += (_, _) =>
            {
                AddressText.Text = core.Source;
                UpdateNav();
            };
            return true;
        }
        catch (Exception ex)
            when (ex
                    is WebView2RuntimeNotFoundException
                        or COMException
                        or InvalidOperationException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            if (_web is not null)
            {
                WebHost.Children.Remove(_web);
                _web.Dispose();
                _web = null;
            }
            return false;
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // Capture the game when the download begins, independently of later sidebar changes.
        var gameRoot = DownloadGameRoot;
        if (
            gameRoot is null
            || !MainWindow.IsSupportedFile(e.ResultFilePath)
            || !LinkSafety.IsSafeWebUrl(e.DownloadOperation.Uri)
        )
        {
            e.Cancel = true;
            return;
        }
        var downloads = new ManagedDownloads();
        DownloadReceipt receipt;
        try
        {
            receipt = downloads.Begin(
                Path.GetFileName(e.ResultFilePath),
                OriginUrl ?? e.DownloadOperation.Uri,
                gameRoot
            );
            e.ResultFilePath = receipt.File;
            e.Handled = true;
            DownloadStarted?.Invoke(receipt);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            e.Cancel = true;
            return;
        }
        var operation = e.DownloadOperation;
        _downloads[receipt.Id] = operation;
        bool finished = false;
        operation.StateChanged += async (_, _) =>
        {
            if (finished)
                return;
            if (operation.State == CoreWebView2DownloadState.Interrupted)
            {
                finished = true;
                _downloads.Remove(receipt.Id);
                DownloadFailed?.Invoke(receipt);
                return;
            }
            if (operation.State != CoreWebView2DownloadState.Completed)
                return;
            finished = true;
            _downloads.Remove(receipt.Id);
            try
            {
                var complete = await Task.Run(() => downloads.Complete(receipt.Id));
                DownloadCompleted?.Invoke(complete);
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or InvalidDataException
                            or UnauthorizedAccessException
                            or InvalidOperationException
                )
            {
                DownloadFailed?.Invoke(receipt);
            }
        };
    }

    private void UpdateNav()
    {
        if (_web?.CoreWebView2 is not { } c)
            return;
        BackButton.IsEnabled = c.CanGoBack;
        ForwardButton.IsEnabled = c.CanGoForward;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_web?.CoreWebView2?.CanGoBack == true)
            _web.CoreWebView2.GoBack();
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (_web?.CoreWebView2?.CanGoForward == true)
            _web.CoreWebView2.GoForward();
    }

    private void OnReload(object sender, RoutedEventArgs e) => _web?.CoreWebView2?.Reload();

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnExternal(object sender, RoutedEventArgs e)
    {
        if (_web?.CoreWebView2?.Source is { } src && Uri.TryCreate(src, UriKind.Absolute, out var u))
            OpenExternal(u);
    }

    /// <summary>System browser, only for validated https URLs (a link cannot launch a file or a command).</summary>
    public static bool OpenExternal(Uri uri)
    {
        if (!LinkSafety.IsSafeWebUrl(uri.AbsoluteUri, out var safe) || safe is null)
            return false;
        try
        {
            Process.Start(new ProcessStartInfo(safe.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Disposes WebView2 when Mod Hub exits (the browser processes end together with the window).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var operation in _downloads.Values.ToArray())
            operation.Cancel();
        _downloads.Clear();
        _web?.Dispose();
        _web = null;
    }
}
