using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CodexSyncWizard.Services;

namespace CodexSyncWizard.Avalonia;

public partial class ConversationsWindow : Window
{
    private string _codexHome = "";
    private string _provider = "";
    private List<ConversationInfo> _all = new();
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _rowChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(CheckBox GroupCheck, List<string> Paths)> _groups = new();
    private bool _suppressEvents;

    public bool DataChanged { get; private set; }

    public ConversationsWindow() { InitializeComponent(); }

    public ConversationsWindow(string codexHome, string provider, IEnumerable<string> allProviders) : this()
    {
        _codexHome = codexHome;
        _provider = provider;

        TargetCombo.ItemsSource = allProviders.Where(p => p != provider).ToList();

        SelectAllCheck.IsCheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            var on = SelectAllCheck.IsChecked == true;
            _suppressEvents = true;
            try
            {
                _selected.Clear();
                if (on)
                {
                    foreach (var c in _all)
                        if (!string.IsNullOrEmpty(c.FilePath)) _selected.Add(c.FilePath);
                }
                foreach (var kv in _rowChecks) kv.Value.IsChecked = _selected.Contains(kv.Key);
                foreach (var g in _groups) g.GroupCheck.IsChecked = on;
            }
            finally { _suppressEvents = false; }
            UpdateSelectionLabel();
        };

        TargetCombo.SelectionChanged += (_, _) => UpdateMoveButton();

        Opened += (_, _) => Load();
    }

    private async void Load()
    {
        HeaderTitle.Text = $"渠道：{_provider}";
        HeaderSub.Text = "扫描中...";

        var home = _codexHome;
        var prov = _provider;
        await Task.Run(() => { _all = ConversationBrowser.ListByProvider(home, prov); });

        var groups = _all
            .GroupBy(c => NormalizeCwd(c.Cwd))
            .Select(g =>
            {
                var items = g.OrderByDescending(c => c.Timestamp ?? DateTime.MinValue).ToList();
                var displayCwd = items.Select(i => i.Cwd).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? g.Key;
                return new { Key = g.Key, DisplayCwd = displayCwd, Items = items };
            })
            .OrderByDescending(g => g.Items.Max(c => c.Timestamp ?? DateTime.MinValue))
            .ToList();

        HeaderSub.Text = $"共 {_all.Count} 个对话，分布在 {groups.Count} 个项目";

        ListPanel.Children.Clear();
        _rowChecks.Clear();
        _groups.Clear();
        _selected.Clear();

        if (_all.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = "（该渠道下没有对话）",
                Classes = { "muted" },
                Margin = new Thickness(0, 8, 0, 0)
            });
            UpdateSelectionLabel();
            return;
        }

        bool first = true;
        foreach (var g in groups)
        {
            ListPanel.Children.Add(MakeGroup(g.DisplayCwd, g.Items, expanded: first));
            first = false;
        }

        UpdateSelectionLabel();
    }

    private Control MakeGroup(string cwd, List<ConversationInfo> items, bool expanded)
    {
        var projectName = ShortProjectName(cwd);
        var subtitle = items.Count + " 个对话";
        if (cwd != projectName) subtitle += "  ·  " + cwd;

        var groupCheck = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(new TextBlock
        {
            Text = projectName,
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = (IBrush)Application.Current!.FindResource("AccentBrush")!
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Classes = { "muted" },
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(groupCheck, 0);
        header.Children.Add(groupCheck);
        Grid.SetColumn(stack, 1);
        header.Children.Add(stack);

        var countBadge = new TextBlock
        {
            Text = $"{items.Count}",
            Classes = { "muted" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(countBadge, 2);
        header.Children.Add(countBadge);

        var content = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        var groupPaths = new List<string>();
        foreach (var c in items)
        {
            if (string.IsNullOrEmpty(c.FilePath)) continue;
            groupPaths.Add(c.FilePath);
            content.Children.Add(MakeRow(c, groupCheck));
        }

        groupCheck.IsCheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            var on = groupCheck.IsChecked == true;
            _suppressEvents = true;
            try
            {
                foreach (var p in groupPaths)
                {
                    if (on) _selected.Add(p); else _selected.Remove(p);
                    if (_rowChecks.TryGetValue(p, out var cb)) cb.IsChecked = on;
                }
            }
            finally { _suppressEvents = false; }
            SyncSelectAllState();
            UpdateSelectionLabel();
        };

        _groups.Add((groupCheck, groupPaths));

        var expander = new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = expanded,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 6, 8, 6),
            Background = (IBrush)Application.Current!.FindResource("PanelBrush")!,
            BorderBrush = (IBrush)Application.Current!.FindResource("BorderBrush2")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        ToolTip.SetTip(expander, cwd);
        return expander;
    }

    private static string ShortProjectName(string cwd)
    {
        if (string.IsNullOrEmpty(cwd) || cwd == "(未指定项目)") return "(未指定项目)";
        try
        {
            var s = cwd.TrimEnd('/', '\\');
            var idx = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
            if (idx >= 0 && idx < s.Length - 1) return s.Substring(idx + 1);
            return s;
        }
        catch { return cwd; }
    }

    private static string NormalizeCwd(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return "(未指定项目)";
        var s = cwd.Trim();
        s = s.Replace('/', '\\');
        while (s.Contains("\\\\")) s = s.Replace("\\\\", "\\");
        s = s.TrimEnd('\\');
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            s = s.ToLowerInvariant();
        return s;
    }

    private Border MakeRow(ConversationInfo c, CheckBox groupCheck)
    {
        var border = new Border
        {
            Background = (IBrush)Application.Current!.FindResource("CardBrush")!,
            BorderBrush = (IBrush)Application.Current!.FindResource("BorderBrush2")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 12, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var rowCheck = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        if (!string.IsNullOrEmpty(c.FilePath)) _rowChecks[c.FilePath] = rowCheck;

        rowCheck.IsCheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            var path = c.FilePath;
            if (string.IsNullOrEmpty(path)) return;
            if (rowCheck.IsChecked == true) _selected.Add(path); else _selected.Remove(path);
            SyncGroupCheck(groupCheck);
            SyncSelectAllState();
            UpdateSelectionLabel();
        };

        Grid.SetColumn(rowCheck, 0);
        grid.Children.Add(rowCheck);

        var stack = new StackPanel { Spacing = 2 };
        var titleText = !string.IsNullOrEmpty(c.Title) ? c.Title
                       : !string.IsNullOrEmpty(c.FirstUserMessage) ? c.FirstUserMessage
                       : "（无标题）";
        stack.Children.Add(new TextBlock
        {
            Text = Truncate(titleText, 120),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = (IBrush)Application.Current!.FindResource("TextBrush")!,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (!string.IsNullOrEmpty(c.FirstUserMessage) && !string.Equals(c.FirstUserMessage, c.Title, StringComparison.Ordinal))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "» " + Truncate(c.FirstUserMessage, 120),
                FontSize = 11,
                Foreground = (IBrush)Application.Current!.FindResource("MutedBrush")!,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        var metaParts = new List<string>();
        if (c.Timestamp != null) metaParts.Add(c.Timestamp.Value.ToString("yyyy-MM-dd HH:mm"));
        if (!string.IsNullOrEmpty(c.Model)) metaParts.Add(c.Model!);
        stack.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", metaParts),
            Classes = { "muted" },
            FontSize = 10
        });

        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        border.Child = grid;

        var hover = (IBrush)Application.Current!.FindResource("CardHoverBrush")!;
        var rest = (IBrush)Application.Current!.FindResource("CardBrush")!;
        border.PointerEntered += (_, _) => border.Background = hover;
        border.PointerExited += (_, _) => border.Background = rest;
        if (!string.IsNullOrEmpty(c.FilePath))
        {
            border.DoubleTapped += (_, _) => OpenInExplorer(c.FilePath);
            ToolTip.SetTip(border, c.FilePath);
        }
        return border;
    }

    private void SyncGroupCheck(CheckBox groupCheck)
    {
        var g = _groups.FirstOrDefault(x => x.GroupCheck == groupCheck);
        if (g.GroupCheck == null) return;
        var allOn = g.Paths.Count > 0 && g.Paths.All(p => _selected.Contains(p));
        var someOn = g.Paths.Any(p => _selected.Contains(p));
        _suppressEvents = true;
        try
        {
            groupCheck.IsThreeState = someOn && !allOn;
            groupCheck.IsChecked = allOn ? true : someOn ? null : false;
        }
        finally { _suppressEvents = false; }
    }

    private void SyncSelectAllState()
    {
        _suppressEvents = true;
        try
        {
            var total = _all.Count(c => !string.IsNullOrEmpty(c.FilePath));
            if (_selected.Count == 0) { SelectAllCheck.IsThreeState = false; SelectAllCheck.IsChecked = false; }
            else if (_selected.Count == total) { SelectAllCheck.IsThreeState = false; SelectAllCheck.IsChecked = true; }
            else { SelectAllCheck.IsThreeState = true; SelectAllCheck.IsChecked = null; }
        }
        finally { _suppressEvents = false; }
    }

    private void UpdateSelectionLabel()
    {
        SelectionLabel.Text = _selected.Count == 0 ? "未选中" : $"已选 {_selected.Count} 个";
        UpdateMoveButton();
    }

    private void UpdateMoveButton()
    {
        MoveBtn.IsEnabled = _selected.Count > 0 && TargetCombo.SelectedItem is string;
        DeleteBtn.IsEnabled = _selected.Count > 0;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", $"-R \"{path}\"");
            else
                Process.Start("xdg-open", $"\"{Path.GetDirectoryName(path)}\"");
        }
        catch { }
    }

    private async void OnMove(object? sender, RoutedEventArgs e)
    {
        var target = TargetCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(target) || _selected.Count == 0) return;

        var setDefault = await ConfirmMigrateAsync(target, _selected.Count);
        if (setDefault == null) return;

        MoveBtn.IsEnabled = false;
        FooterHint.Text = "正在迁移...";

        try
        {
            var files = _selected.ToList();
            var home = _codexHome;
            var t = target;
            var result = await Task.Run(() => SessionSyncer.SyncSpecificFiles(home, files, t));
            if (setDefault == true)
                await Task.Run(() => ConfigService.WriteProvider(home, t));

            DataChanged = true;
            var extra = setDefault == true ? "\n顺便把 config.toml 默认渠道改为：" + t : "";
            await Dialogs.InfoAsync(this, "完成",
                $"已迁移 {result.RolloutFilesSynced} 个对话，数据库 {result.SqliteRowsSynced} 条记录。{extra}\n\n备份：{result.BackupPath}");
            Close();
        }
        catch (SqliteLockedException ex)
        {
            await Dialogs.InfoAsync(this, "Codex 客户端正在运行", ex.Message);
            FooterHint.Text = "勾选后选择目标渠道，点「迁移选中」";
            MoveBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "错误", ex.Message);
            FooterHint.Text = "勾选后选择目标渠道，点「迁移选中」";
            MoveBtn.IsEnabled = true;
        }
    }

    private async Task<bool?> ConfirmMigrateAsync(string target, int count)
    {
        var setDefault = false;
        var dlg = new Window
        {
            Title = "确认迁移",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.FindResource("BgBrush")!,
            ShowInTaskbar = false
        };
        var msg = new TextBlock
        {
            Text = $"把选中的 {count} 个对话从「{_provider}」迁移到「{target}」？\n会先自动备份。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (IBrush)Application.Current!.FindResource("TextBrush")!,
            FontSize = 13,
            Margin = new Thickness(20, 20, 20, 8)
        };
        var check = new CheckBox
        {
            Content = $"顺便把 config.toml 默认渠道也改成「{target}」",
            Foreground = (IBrush)Application.Current!.FindResource("TextBrush")!,
            FontSize = 12,
            Margin = new Thickness(20, 0, 20, 8)
        };
        check.IsCheckedChanged += (_, _) => setDefault = check.IsChecked == true;

        var okBtn = new Button { Content = "确定", MinWidth = 80, Margin = new Thickness(4, 0, 0, 0) };
        okBtn.Classes.Add("primary");
        var cancelBtn = new Button { Content = "取消", MinWidth = 80 };
        bool confirmed = false;
        okBtn.Click += (_, _) => { confirmed = true; dlg.Close(); };
        cancelBtn.Click += (_, _) => dlg.Close();

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 16),
            Spacing = 8,
            Children = { cancelBtn, okBtn }
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(msg, 0);
        Grid.SetRow(check, 1);
        Grid.SetRow(btns, 2);
        grid.Children.Add(msg);
        grid.Children.Add(check);
        grid.Children.Add(btns);
        dlg.Content = grid;
        await dlg.ShowDialog(this);
        if (!confirmed) return null;
        return setDefault;
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0) return;
        var ok = await Dialogs.ConfirmAsync(this, "确认删除",
            $"⚠ 永久删除选中的 {_selected.Count} 个对话？\n\n会先自动备份，万一可在「高级」中还原。");
        if (!ok) return;

        DeleteBtn.IsEnabled = false;
        MoveBtn.IsEnabled = false;
        FooterHint.Text = "正在删除...";

        try
        {
            var files = _selected.ToList();
            var home = _codexHome;
            var result = await Task.Run(() => SessionSyncer.DeleteSpecificFiles(home, files));
            DataChanged = true;
            await Dialogs.InfoAsync(this, "完成",
                $"已删除 {result.RolloutFilesSynced} 个对话，数据库 {result.SqliteRowsSynced} 条记录。\n\n备份：{result.BackupPath}");
            Close();
        }
        catch (SqliteLockedException ex)
        {
            await Dialogs.InfoAsync(this, "Codex 客户端正在运行", ex.Message);
            FooterHint.Text = "勾选后选择目标渠道，点「迁移选中」";
            UpdateMoveButton();
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "错误", ex.Message);
            FooterHint.Text = "勾选后选择目标渠道，点「迁移选中」";
            UpdateMoveButton();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
