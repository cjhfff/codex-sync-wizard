using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CodexSyncWizard.Services;

namespace CodexSyncWizard.Avalonia;

public partial class BulkOperationsWindow : Window
{
    private readonly string _codexHome;
    private ScanResult? _scan;
    public bool DataChanged { get; private set; }

    public BulkOperationsWindow() : this(CodexHomeService.GetDefaultPath()) { }

    public BulkOperationsWindow(string codexHome)
    {
        _codexHome = codexHome;
        InitializeComponent();
        Opened += (_, _) => Reload();
        MigrateFrom.SelectionChanged += (_, _) => UpdateButtons();
        MigrateTo.SelectionChanged += (_, _) => UpdateButtons();
        DeleteProvider.SelectionChanged += (_, _) => UpdateButtons();
    }

    private void Reload()
    {
        try { _scan = ProviderScanner.Scan(_codexHome); }
        catch (Exception ex) { Log("扫描失败: " + ex.Message); return; }

        var names = _scan.Providers.Keys.OrderBy(x => x).ToList();
        MigrateFrom.ItemsSource = names;
        MigrateTo.ItemsSource = names;
        DeleteProvider.ItemsSource = names;
        StatusLabel.Text = $"共 {_scan.Providers.Count} 个 provider，{_scan.TotalSqliteThreads} 条对话";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var from = MigrateFrom.SelectedItem as string;
        var to = MigrateTo.SelectedItem as string;
        MigrateBtn.IsEnabled = !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to) && from != to;
        MergeAllBtn.IsEnabled = !string.IsNullOrEmpty(to);
        DeleteBtn.IsEnabled = !string.IsNullOrEmpty(DeleteProvider.SelectedItem as string);
    }

    private void Log(string s)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text = (LogBox.Text ?? "") + $"[{DateTime.Now:HH:mm:ss}] {s}\n";
            LogScroll.ScrollToEnd();
        });
    }

    private async void OnMigrate(object? sender, RoutedEventArgs e)
    {
        var from = (MigrateFrom.SelectedItem as string)!;
        var to = (MigrateTo.SelectedItem as string)!;
        var convs = ConversationBrowser.ListByProvider(_codexHome, from);
        var paths = convs.Where(c => !string.IsNullOrEmpty(c.FilePath)).Select(c => c.FilePath!).ToList();

        var ok = await Dialogs.ConfirmAsync(this, "确认迁移",
            $"把 provider「{from}」下的 {paths.Count} 个对话全部迁移到「{to}」？\n\n会先自动备份。");
        if (!ok) return;
        await DoMigrateAsync(paths, to);
    }

    private async void OnMergeAll(object? sender, RoutedEventArgs e)
    {
        var to = (MigrateTo.SelectedItem as string)!;
        if (_scan == null) return;
        var paths = new List<string>();
        foreach (var p in _scan.Providers.Values)
        {
            if (p.Name == to) continue;
            var convs = ConversationBrowser.ListByProvider(_codexHome, p.Name);
            paths.AddRange(convs.Where(c => !string.IsNullOrEmpty(c.FilePath)).Select(c => c.FilePath!));
        }

        var ok = await Dialogs.ConfirmAsync(this, "确认合并",
            $"把所有非「{to}」的对话（{paths.Count} 个）全部合并到「{to}」？\n\n会先自动备份。");
        if (!ok) return;
        await DoMigrateAsync(paths, to);
    }

    private async Task DoMigrateAsync(List<string> paths, string target)
    {
        if (paths.Count == 0) { Log("没有可迁移的对话"); return; }

        MigrateBtn.IsEnabled = MergeAllBtn.IsEnabled = DeleteBtn.IsEnabled = false;
        var progress = new Progress<string>(Log);
        try
        {
            var home = _codexHome;
            var t = target;
            var result = await Task.Run(() => SessionSyncer.SyncSpecificFiles(home, paths, t, progress));
            Log($"完成: 改 {result.RolloutFilesSynced} 个对话 / {result.SqliteRowsSynced} 条数据库");
            Log($"备份: {result.BackupPath}");
            DataChanged = true;
            Reload();
        }
        catch (SqliteLockedException ex)
        {
            Log("✗ " + ex.Message);
            await Dialogs.InfoAsync(this, "Codex 客户端正在运行", ex.Message);
        }
        catch (Exception ex)
        {
            Log("✗ " + ex.Message);
        }
        finally { UpdateButtons(); }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        var provider = (DeleteProvider.SelectedItem as string)!;
        var convs = ConversationBrowser.ListByProvider(_codexHome, provider);
        var paths = convs.Where(c => !string.IsNullOrEmpty(c.FilePath)).Select(c => c.FilePath!).ToList();

        var ok = await Dialogs.ConfirmAsync(this, "确认删除",
            $"⚠ 永久删除 provider「{provider}」下的 {paths.Count} 个对话？\n\n会先自动备份，可还原。");
        if (!ok) return;

        if (paths.Count == 0) { Log("没有可删除的对话"); return; }

        MigrateBtn.IsEnabled = MergeAllBtn.IsEnabled = DeleteBtn.IsEnabled = false;
        var progress = new Progress<string>(Log);
        try
        {
            var home = _codexHome;
            var result = await Task.Run(() => SessionSyncer.DeleteSpecificFiles(home, paths, progress));
            Log($"完成: 删除 {result.RolloutFilesSynced} 个对话 / {result.SqliteRowsSynced} 条数据库");
            Log($"备份: {result.BackupPath}");
            DataChanged = true;
            Reload();
        }
        catch (SqliteLockedException ex)
        {
            Log("✗ " + ex.Message);
            await Dialogs.InfoAsync(this, "Codex 客户端正在运行", ex.Message);
        }
        catch (Exception ex)
        {
            Log("✗ " + ex.Message);
        }
        finally { UpdateButtons(); }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
