using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CodexSyncWizard.Services;
using DragDrop = Avalonia.Input.DragDrop;

namespace CodexSyncWizard.Avalonia;

public partial class MainWindow : Window
{
    private string _codexHome = "";
    private ScanResult? _scan;
    private string? _currentProvider;
    private AppSettings _settings = new();
    private string? _updateUrl;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        if (!string.IsNullOrEmpty(_settings.CodexHome) && Directory.Exists(_settings.CodexHome))
            _codexHome = _settings.CodexHome;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Opened += async (_, _) =>
        {
            UpdateStatusPill();
            await DetectAsync();
            _ = CheckForUpdatesAsync();
        };
    }

    public async void RequestRefresh()
    {
        await DetectAsync();
        UpdateStatusPill();
    }

    public void UpdateStatusPill()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var watching = App.Current?.IsWatching == true;
            StatusDot.Fill = watching
                ? (IBrush)Application.Current!.FindResource("AccentBrush")!
                : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            StatusPillText.Text = watching ? "监听中" : "待命";
        });
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateCheckService.CheckAsync();
            if (info != null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _updateUrl = info.DownloadUrl;
                    UpdateLink.Content = $"有新版 v{info.LatestVersion}";
                    UpdateLink.IsVisible = true;
                });
            }
        }
        catch { }
    }

    private void OnUpdateLink(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_updateUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _updateUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files)) e.DragEffects = DragDropEffects.Link;
        else e.DragEffects = DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        var files = e.Data.GetFiles();
        if (files == null) return;
        foreach (var f in files)
        {
            var path = f.Path.LocalPath;
            if (Directory.Exists(path))
            {
                _codexHome = path;
                _settings.CodexHome = path;
                SettingsService.Save(_settings);
                await DetectAsync();
                return;
            }
        }
    }

    private async void OnChangeDir(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 .codex 目录",
            AllowMultiple = false
        });
        if (folders.Count > 0)
        {
            _codexHome = folders[0].Path.LocalPath;
            _settings.CodexHome = _codexHome;
            SettingsService.Save(_settings);
            await DetectAsync();
        }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await DetectAsync();

    private async Task DetectAsync()
    {
        if (string.IsNullOrEmpty(_codexHome))
            _codexHome = CodexHomeService.GetDefaultPath();

        PathLabel.Text = _codexHome;
        ProvidersWrap.ItemsSource = null;

        if (!CodexHomeService.Exists(_codexHome))
        {
            StatusLabel.Text = "未找到 Codex 目录";
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                _scan = ProviderScanner.Scan(_codexHome);
                _currentProvider = ConfigService.ReadProvider(_codexHome);
            });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"扫描失败：{ex.Message}";
            return;
        }

        var totalConvs = _scan!.TotalRolloutFiles + _scan.TotalArchivedFiles;
        StatusLabel.Text = $"{totalConvs} 个对话 / {_scan.TotalSqliteThreads} 条数据库记录 / {_scan.Providers.Count} 个渠道  ·  点击渠道卡片查看与迁移";

        var cards = new List<Control>();
        foreach (var p in _scan.Providers.Values.OrderByDescending(p => p.TotalCount))
            cards.Add(MakeProviderCard(p));

        ProvidersWrap.ItemsSource = cards;
    }

    private Border MakeProviderCard(ProviderInfo info)
    {
        var isCurrent = !string.IsNullOrEmpty(_currentProvider) && _currentProvider == info.Name;
        var accent = (IBrush)Application.Current!.FindResource("AccentBrush")!;
        var muted = (IBrush)Application.Current!.FindResource("MutedBrush")!;
        var card = (IBrush)Application.Current!.FindResource("CardBrush")!;
        var hover = (IBrush)Application.Current!.FindResource("CardHoverBrush")!;
        var border2 = (IBrush)Application.Current!.FindResource("BorderBrush2")!;

        var border = new Border
        {
            Background = card,
            BorderBrush = border2,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 12, 12),
            Width = 280,
            Height = 132,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // Header row: name + (在用 badge) + count
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var nameStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(new TextBlock
        {
            Text = info.Name,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = accent
        });
        if (isCurrent)
        {
            var badge = new Border
            {
                Background = accent,
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 1)
            };
            badge.Child = new TextBlock
            {
                Text = "在用",
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White
            };
            nameStack.Children.Add(badge);
        }
        Grid.SetColumn(nameStack, 0);
        header.Children.Add(nameStack);

        var countLabel = new TextBlock
        {
            Text = info.TotalCount.ToString(),
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countLabel, 1);
        header.Children.Add(countLabel);

        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var subtitle = new TextBlock
        {
            Text = $"对话 {info.RolloutCount}  ·  数据库 {info.SqliteCount}",
            Classes = { "muted" },
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(subtitle, 1);
        grid.Children.Add(subtitle);

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        bottom.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var openHint = new TextBlock
        {
            Text = "查看 / 迁移 ›",
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(openHint, 0);
        bottom.Children.Add(openHint);

        Button? setDefaultLink = null;
        if (!isCurrent)
        {
            setDefaultLink = new Button
            {
                Content = "设为默认",
                FontSize = 11,
                Padding = new Thickness(2, 1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = muted,
                Cursor = new Cursor(StandardCursorType.Hand),
                IsVisible = false
            };
            setDefaultLink.Classes.Add("link");
            setDefaultLink.Click += async (s, _) =>
            {
                if (s is Button btn) { btn.IsEnabled = false; }
                try
                {
                    var providerName = info.Name;
                    var home = _codexHome;
                    if (!ConfigService.IsProviderDefined(home, providerName))
                    {
                        await Dialogs.InfoAsync(this, "无法设为默认",
                            $"config.toml 里没有 [model_providers.{providerName}] 定义，先在 config.toml 中加上才能切。");
                        return;
                    }
                    var ok = await Dialogs.ConfirmAsync(this, "确认",
                        $"把 config.toml 默认渠道改成「{providerName}」？\n（这是 Codex 启动时使用的渠道）");
                    if (!ok) return;
                    await Task.Run(() => ConfigService.WriteProvider(home, providerName));
                    await DetectAsync();
                }
                finally
                {
                    if (s is Button btn2) btn2.IsEnabled = true;
                }
            };
            Grid.SetColumn(setDefaultLink, 1);
            bottom.Children.Add(setDefaultLink);
        }

        Grid.SetRow(bottom, 3);
        grid.Children.Add(bottom);

        border.Child = grid;

        var providerName = info.Name;
        border.PointerEntered += (_, _) =>
        {
            border.Background = hover;
            if (setDefaultLink != null) setDefaultLink.IsVisible = true;
        };
        border.PointerExited += (_, _) =>
        {
            border.Background = card;
            if (setDefaultLink != null) setDefaultLink.IsVisible = false;
        };
        border.PointerPressed += async (_, e) =>
        {
            if (setDefaultLink != null && e.Source is Control src)
            {
                if (src == setDefaultLink || (src is TextBlock tb && tb.Parent == setDefaultLink)) return;
            }
            var providers = _scan?.Providers.Keys.ToList() ?? new List<string> { providerName };
            var dlg = new ConversationsWindow(_codexHome, providerName, providers);
            await dlg.ShowDialog(this);
            if (dlg.DataChanged) await DetectAsync();
        };

        return border;
    }

    private async void OnAdvanced(object? sender, RoutedEventArgs e)
    {
        var dlg = new AdvancedWindow(_codexHome);
        await dlg.ShowDialog(this);

        if (!string.IsNullOrEmpty(dlg.ManualProvider))
        {
            var providers = _scan?.Providers.Keys.ToList() ?? new List<string>();
            if (!providers.Contains(dlg.ManualProvider!)) providers.Add(dlg.ManualProvider!);
            var win = new ConversationsWindow(_codexHome, _currentProvider ?? providers[0], providers);
            await win.ShowDialog(this);
            if (win.DataChanged) await DetectAsync();
        }

        if (dlg.RestoredSomething) await DetectAsync();
    }
}
