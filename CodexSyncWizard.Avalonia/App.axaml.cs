using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using CodexSyncWizard.Services;

namespace CodexSyncWizard.Avalonia;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private ConfigWatcher? _watcher;
    private AppSettings _settings = new();
    private readonly object _syncLock = new();
    private bool _autoSyncRunning;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _watchStatusMenuItem;

    public static new App? Current => Application.Current as App;

    public WindowNotificationManager? NotificationManager { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _settings = SettingsService.Load();

            _mainWindow = new MainWindow();
            _mainWindow.Closing += OnMainWindowClosing;
            desktop.MainWindow = _mainWindow;
            _mainWindow.Show();

            NotificationManager = new WindowNotificationManager(_mainWindow)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3
            };

            BuildTray();

            if (_settings.AutoWatchEnabled)
                StartWatcher();
            else
                UpdateTrayStatus();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void BuildTray()
    {
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://CodexSyncWizard/app.ico"));
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                ToolTipText = "Codex 对话同步"
            };
            _trayIcon.Clicked += (_, _) => OpenMainWindow();

            var menu = new NativeMenu();

            var openItem = new NativeMenuItem("打开主窗口");
            openItem.Click += (_, _) => OpenMainWindow();
            menu.Items.Add(openItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            _watchStatusMenuItem = new NativeMenuItem("后台监听: 关闭") { IsEnabled = false };
            menu.Items.Add(_watchStatusMenuItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += (_, _) => ExitApp();
            menu.Items.Add(exitItem);

            _trayIcon.Menu = menu;

            var icons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(this, icons);
        }
        catch { }
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var s = SettingsService.Load();
        if (s.AutoWatchEnabled && _mainWindow != null)
        {
            e.Cancel = true;
            _mainWindow.Hide();
            ShowNotification("已最小化到托盘", "继续在后台监听 config.toml 变化。");
        }
    }

    public void StartWatcher()
    {
        var home = !string.IsNullOrEmpty(_settings.CodexHome) && Directory.Exists(_settings.CodexHome)
            ? _settings.CodexHome
            : CodexHomeService.GetDefaultPath();
        if (!Directory.Exists(home)) return;

        StopWatcher();
        _watcher = new ConfigWatcher(home);
        _watcher.ProviderChanged += OnProviderChanged;
        _watcher.Start();
        UpdateTrayStatus();
    }

    public void StopWatcher()
    {
        if (_watcher != null)
        {
            _watcher.ProviderChanged -= OnProviderChanged;
            _watcher.Dispose();
            _watcher = null;
        }
        UpdateTrayStatus();
    }

    public void RefreshFromSettings()
    {
        _settings = SettingsService.Load();
        if (_settings.AutoWatchEnabled)
            StartWatcher();
        else
            StopWatcher();
        Dispatcher.UIThread.Post(() => _mainWindow?.UpdateStatusPill());
    }

    public bool IsWatching => _watcher != null;

    private void UpdateTrayStatus()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_watchStatusMenuItem != null)
                _watchStatusMenuItem.Header = IsWatching ? "后台监听: 开" : "后台监听: 关闭";
            if (_trayIcon != null)
                _trayIcon.ToolTipText = IsWatching
                    ? $"Codex 对话同步 — 监听中（{_watcher?.CurrentProvider ?? "?"}）"
                    : "Codex 对话同步";
        });
    }

    private async void OnProviderChanged(object? sender, ProviderChangedEventArgs e)
    {
        var s = SettingsService.Load();
        var home = !string.IsNullOrEmpty(s.CodexHome) ? s.CodexHome : CodexHomeService.GetDefaultPath();
        var newProvider = e.NewProvider;

        if (string.IsNullOrEmpty(newProvider))
        {
            ShowNotification("config.toml 变化", "顶层 model_provider 被清空，已忽略。");
            return;
        }

        if (!ConfigService.IsProviderDefined(home, newProvider))
        {
            ShowNotification($"⚠ 切到 {newProvider}",
                $"config.toml 没有 [model_providers.{newProvider}] 定义，未自动同步。");
            return;
        }

        if (!s.AutoMergeOnChange)
        {
            ShowNotification($"检测到切换：{e.OldProvider} → {newProvider}",
                "已禁用「自动合并」，请手动处理。");
            return;
        }

        lock (_syncLock)
        {
            if (_autoSyncRunning) return;
            _autoSyncRunning = true;
        }

        try
        {
            if (SessionSyncer.IsCodexLikelyRunning(home))
            {
                ShowNotification($"切到 {newProvider}",
                    "Codex 客户端正在运行，未自动同步。请关闭 Codex 后再处理。");
                return;
            }

            var result = await Task.Run(() =>
                SessionSyncer.Sync(home, newProvider, false, SyncMode.MergeToTarget));
            ShowNotification($"已自动同步到 {newProvider}",
                $"{result.RolloutFilesSynced} 个对话 / {result.SqliteRowsSynced} 条记录已合并。");
            UpdateTrayStatus();
            Dispatcher.UIThread.Post(() => _mainWindow?.RequestRefresh());
        }
        catch (Exception ex)
        {
            ShowNotification("自动同步失败", ex.Message);
        }
        finally
        {
            lock (_syncLock) { _autoSyncRunning = false; }
        }
    }

    public void ShowNotification(string title, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_mainWindow != null && _mainWindow.IsVisible)
                {
                    NotificationManager?.Show(new Notification(title, message,
                        NotificationType.Information, TimeSpan.FromSeconds(6)));
                }
                else if (_trayIcon != null)
                {
                    _trayIcon.ToolTipText = $"{title}\n{message}";
                }
            }
            catch { }
        });
    }

    private void OpenMainWindow()
    {
        if (_mainWindow == null) return;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.RequestRefresh();
    }

    private void ExitApp()
    {
        StopWatcher();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
