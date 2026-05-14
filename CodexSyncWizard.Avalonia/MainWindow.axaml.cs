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

        UpdateConsolidatePanel();
    }

    private void UpdateConsolidatePanel()
    {
        if (_scan == null) return;
        var providers = _scan.Providers.Keys.OrderBy(x => x).ToList();
        var keepSelection = ConsolidateTarget.SelectedItem as string;
        ConsolidateTarget.SelectionChanged -= OnConsolidateTargetChanged;
        ConsolidateTarget.ItemsSource = providers;
        if (!string.IsNullOrEmpty(keepSelection) && providers.Contains(keepSelection))
            ConsolidateTarget.SelectedItem = keepSelection;
        else if (!string.IsNullOrEmpty(_currentProvider) && providers.Contains(_currentProvider))
            ConsolidateTarget.SelectedItem = _currentProvider;
        else if (providers.Count > 0)
            ConsolidateTarget.SelectedIndex = 0;
        ConsolidateTarget.SelectionChanged += OnConsolidateTargetChanged;
        UpdateConsolidatePlanText();
    }

    private void OnConsolidateTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateConsolidatePlanText();
    }

    private void UpdateConsolidatePlanText()
    {
        var target = ConsolidateTarget.SelectedItem as string;
        if (string.IsNullOrEmpty(target) || _scan == null)
        {
            ConsolidatePlan.Text = "";
            ConsolidateBtn.IsEnabled = false;
            return;
        }
        int otherCount = 0;
        foreach (var p in _scan.Providers.Values)
            if (p.Name != target) otherCount += p.TotalCount;
        if (otherCount == 0)
        {
            ConsolidatePlan.Text = "（已经全在该渠道下）";
            ConsolidateBtn.IsEnabled = false;
        }
        else
        {
            ConsolidatePlan.Text = $"将合并 {otherCount} 个对话";
            ConsolidateBtn.IsEnabled = true;
        }
    }

    private async void OnConsolidate(object? sender, RoutedEventArgs e)
    {
        var target = ConsolidateTarget.SelectedItem as string;
        if (string.IsNullOrEmpty(target) || _scan == null) return;

        // 归并是数据层操作，必须扫全部（含 subagent / exec），不能被视图 filter 漏掉
        var origInclude = ConversationBrowser.IncludeInternalSources;
        ConversationBrowser.IncludeInternalSources = true;
        ScanResult fullScan;
        try { fullScan = ProviderScanner.Scan(_codexHome, includeInternalSources: true); }
        finally { /* 不在这先复位，下面收集对话还要用 */ }

        var paths = new List<string>();
        var unregistered = new List<string>();
        var seenCwd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int internalCount = 0;
        foreach (var p in fullScan.Providers.Values)
        {
            if (p.Name == target) continue;
            var convs = ConversationBrowser.ListByProvider(_codexHome, p.Name);
            foreach (var c in convs)
            {
                if (string.IsNullOrEmpty(c.FilePath)) continue;
                paths.Add(c.FilePath);
                var cat = SourceCategory.Categorize(c.Source);
                if (cat == SourceCategory.Subagent || cat == SourceCategory.Exec) internalCount++;
                if (!string.IsNullOrEmpty(c.Cwd))
                {
                    var key = WorkspaceRegistryService.Normalize(c.Cwd).ToLowerInvariant();
                    if (seenCwd.Add(key) && !WorkspaceRegistryService.IsRegistered(_codexHome, c.Cwd))
                        unregistered.Add(c.Cwd);
                }
            }
        }
        ConversationBrowser.IncludeInternalSources = origInclude;
        if (paths.Count == 0) return;

        var providerSummary = string.Join(", ",
            fullScan.Providers.Values
                .Where(p => p.Name != target && p.TotalCount > 0)
                .OrderByDescending(p => p.TotalCount)
                .Select(p => $"{p.Name}({p.TotalCount})"));

        var msg =
            $"把这 {paths.Count} 个对话归到「{target}」名下？\n" +
            $"涉及来源: {providerSummary}\n";
        if (internalCount > 0)
            msg += $"（含 {internalCount} 个子 agent / exec 内部对话，平时视图里隐藏的也会一并迁移）\n";
        msg +=
            "\n做了什么:\n" +
            "  · 仅修改对话首行的 model_provider 字段\n" +
            "  · 数据库 threads.model_provider 同步更新\n" +
            "  · 对话内容（消息、上下文、文件）一字不动\n" +
            "  · 老 provider 名字下不再有这些对话（被改名了，不是删除）\n" +
            "  · 操作前自动备份，「高级 → 备份列表」可一键还原";
        if (unregistered.Count > 0)
            msg += $"\n\n顺便会把 {unregistered.Count} 个未登记的项目加入 Codex 工作区列表。";
        var ok = await Dialogs.ConfirmAsync(this, "确认归并", msg);
        if (!ok) return;

        ConsolidateBtn.IsEnabled = false;
        try
        {
            var home = _codexHome;
            var t = target;
            var result = await Task.Run(() => SessionSyncer.SyncSpecificFiles(home, paths, t));

            foreach (var cwd in unregistered)
            {
                var clean = WorkspaceRegistryService.Normalize(cwd);
                if (!Directory.Exists(clean))
                    try { Directory.CreateDirectory(clean); } catch { }
            }
            var batch = unregistered.Count > 0
                ? await Task.Run(() => WorkspaceRegistryService.AddWorkspaces(home, unregistered))
                : new WorkspaceRegistryService.BatchAddResult(0, 0, 0, null);

            await Dialogs.WarnIfPartialSyncAsync(this, result);

            var extras = new List<string> { $"改 {result.RolloutFilesSynced} 个对话 / {result.SqliteRowsSynced} 条数据库" };
            if (batch.Added > 0) extras.Add($"加入 {batch.Added} 个项目到工作区");
            if (batch.AlreadyExists > 0) extras.Add($"{batch.AlreadyExists} 个项目已存在，跳过");
            if (batch.Failed > 0) extras.Add($"⚠ {batch.Failed} 个项目未加入（{batch.ErrorMsg}）");

            await Dialogs.InfoAsync(this, "完成",
                "全部归并完成！\n\n" + string.Join("\n", extras) + "\n\n备份：" + result.BackupPath);
            await DetectAsync();
        }
        catch (ProviderNotDefinedException ex)
        {
            await Dialogs.ShowProviderNotDefinedAsync(this, ex);
        }
        catch (SqliteLockedException ex)
        {
            await Dialogs.InfoAsync(this, "Codex 客户端正在运行", ex.Message);
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "错误", ex.Message);
        }
        finally
        {
            UpdateConsolidatePlanText();
        }
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

        var isEmpty = info.TotalCount == 0;
        var countLabel = new TextBlock
        {
            Text = isEmpty ? "空" : info.TotalCount.ToString(),
            FontSize = isEmpty ? 12 : 22,
            FontWeight = FontWeight.Bold,
            Foreground = isEmpty ? muted : accent,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countLabel, 1);
        header.Children.Add(countLabel);

        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var subtitle = new TextBlock
        {
            Text = isEmpty
                ? "config.toml 已定义，可作为迁入目标"
                : $"对话 {info.RolloutCount}  ·  数据库 {info.SqliteCount}",
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
            Text = isEmpty ? "（无对话可看）" : "查看 / 迁移 ›",
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

    private async void OnBulk(object? sender, RoutedEventArgs e)
    {
        var dlg = new BulkOperationsWindow(_codexHome);
        await dlg.ShowDialog(this);
        if (dlg.DataChanged) await DetectAsync();
    }

    private async void OnCommand(object? sender, RoutedEventArgs e)
    {
        var dlg = new CommandPaletteWindow();
        await dlg.ShowDialog(this);
        await DetectAsync();
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
