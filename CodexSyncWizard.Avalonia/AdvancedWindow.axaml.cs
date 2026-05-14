using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CodexSyncWizard.Services;

namespace CodexSyncWizard.Avalonia;

public partial class AdvancedWindow : Window
{
    private readonly string _codexHome;
    private List<LegacyBackupInfo> _backups = new();

    public string? ManualProvider { get; private set; }
    public bool RestoredSomething { get; private set; }

    public AdvancedWindow() : this(CodexHomeService.GetDefaultPath()) { }

    public AdvancedWindow(string codexHome)
    {
        _codexHome = codexHome;
        InitializeComponent();
        Opened += (_, _) =>
        {
            LoadBackups();
            LoadWatchSettings();
        };
    }

    private void LoadWatchSettings()
    {
        var s = SettingsService.Load();
        AutoWatchCheck.IsChecked = s.AutoWatchEnabled;
        AutoMergeCheck.IsChecked = s.AutoMergeOnChange;

        var ccsInfo = CcSwitchService.GetCurrentCodexProvider();
        if (ccsInfo != null)
            WatchHint.Text = $"已检测到 cc-switch（当前 Codex 渠道：{ccsInfo.CurrentProviderId}）。开启后，cc-switch 切换 provider 时本工具会自动合并对话历史。";
        else
            WatchHint.Text = "监听 ~/.codex/config.toml 顶层 model_provider 变化。无 cc-switch 也可用：手动改 config.toml 或别的工具改了都会触发。";

        AutoWatchCheck.IsCheckedChanged += (_, _) =>
        {
            var cur = SettingsService.Load();
            cur.AutoWatchEnabled = AutoWatchCheck.IsChecked == true;
            SettingsService.Save(cur);
            App.Current?.RefreshFromSettings();
        };
        AutoMergeCheck.IsCheckedChanged += (_, _) =>
        {
            var cur = SettingsService.Load();
            cur.AutoMergeOnChange = AutoMergeCheck.IsChecked == true;
            SettingsService.Save(cur);
        };
    }

    private void OnUseManual(object? sender, RoutedEventArgs e)
    {
        var name = ManualInput.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            _ = Dialogs.InfoAsync(this, "提示", "请输入渠道名");
            return;
        }
        ManualProvider = name;
        Close();
    }

    private void OnOpenBackupDir(object? sender, RoutedEventArgs e)
    {
        var idx = BackupList.SelectedIndex;
        if (idx < 0 || idx >= _backups.Count) return;
        var path = _backups[idx].Path;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start("explorer.exe", $"\"{path}\"");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", $"\"{path}\"");
            else
                Process.Start("xdg-open", $"\"{path}\"");
        }
        catch { }
    }

    private async void OnRestoreSingle(object? sender, RoutedEventArgs e)
    {
        var idx = BackupList.SelectedIndex;
        if (idx < 0 || idx >= _backups.Count)
        {
            await Dialogs.InfoAsync(this, "提示", "请先选一份");
            return;
        }
        await RunSingleRestoreAsync(_backups[idx]);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void LoadBackups()
    {
        _backups = LegacyBackupService.ListAll(_codexHome);
        BackupList.Items.Clear();

        if (_backups.Count == 0)
        {
            BackupList.Items.Add("（无备份）");
        }
        else
        {
            foreach (var b in _backups)
            {
                var time = b.CreatedAt.ToString("MM-dd HH:mm");
                var fmt = b.Format == BackupFormat.Compact ? "紧凑" : "完整";
                var target = string.IsNullOrEmpty(b.TargetProvider) ? "" : $"  → {b.TargetProvider}";
                var changed = b.ChangedFileCount > 0 ? $"  改 {b.ChangedFileCount}" : "";
                BackupList.Items.Add($"{time}  [{fmt}]{target}{changed}");
            }
        }

        var compact = _backups.Where(b => b.Format == BackupFormat.Compact && b.ChangedFileCount > 0).ToList();
        if (compact.Count == 0)
        {
            SmartRestoreInfo.Text = "无紧凑备份可用";
            SmartRestoreBtn.IsEnabled = false;
        }
        else
        {
            var merged = new Dictionary<string, int>();
            foreach (var b in compact)
                foreach (var kv in b.OriginalProviders)
                    merged[kv.Key] = Math.Max(merged.GetValueOrDefault(kv.Key), kv.Value);

            var lines = new List<string> { $"叠加 {compact.Count} 份备份后预计分布：" };
            foreach (var kv in merged.OrderByDescending(k => k.Value))
                lines.Add($"  {kv.Key}  {kv.Value}");
            SmartRestoreInfo.Text = string.Join("\n", lines);
            SmartRestoreBtn.IsEnabled = true;
        }
    }

    private void Log(string msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogBoxWrapper.IsVisible = true;
            LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
            LogScroll.ScrollToEnd();
        });
    }

    private async void OnSmartRestore(object? sender, RoutedEventArgs e)
    {
        var ok = await Dialogs.ConfirmAsync(this, "确认", "执行智能还原？");
        if (!ok) return;

        SmartRestoreBtn.IsEnabled = false;
        LogBox.Text = "";
        var progress = new Progress<string>(Log);

        try
        {
            var home = _codexHome;
            var result = await Task.Run(() => LegacyBackupService.RestoreOriginalDistribution(home, progress));

            Log("");
            Log($"完成 — 处理 {result.FilesRestored} 个对话 / 数据库 {result.SqliteRowsUpdated}");
            foreach (var kv in result.ResultDistribution.OrderByDescending(k => k.Value))
                Log($"  {kv.Key}  {kv.Value}");

            RestoredSomething = true;
            await Dialogs.InfoAsync(this, "完成", "完成，重启 Codex 客户端查看");
            LoadBackups();
        }
        catch (Exception ex)
        {
            Log($"出错：{ex.Message}");
            await Dialogs.InfoAsync(this, "错误", ex.Message);
        }
        finally
        {
            SmartRestoreBtn.IsEnabled = true;
        }
    }

    private async Task RunSingleRestoreAsync(LegacyBackupInfo info)
    {
        string desc;
        if (info.Format == BackupFormat.Compact)
        {
            var origStr = info.OriginalProviders.Count > 0
                ? string.Join(", ", info.OriginalProviders.Select(kv => $"{kv.Key}×{kv.Value}"))
                : "无";
            desc = $"{info.CreatedAt:MM-dd HH:mm}  紧凑\n同步前：{origStr}";
        }
        else
        {
            desc = $"{info.CreatedAt:MM-dd HH:mm}  完整";
        }

        var ok = await Dialogs.ConfirmAsync(this, "确认", $"还原此备份？\n\n{desc}");
        if (!ok) return;

        LogBox.Text = "";
        var progress = new Progress<string>(Log);

        try
        {
            var home = _codexHome;
            if (info.Format == BackupFormat.Compact)
            {
                var result = await Task.Run(() => LegacyBackupService.RestoreCompact(home, info.Path, progress));
                Log("");
                Log($"完成 — {result.FilesRestored} 个对话 / 数据库 {result.SqliteRowsUpdated}");
                RestoredSomething = true;
                await Dialogs.InfoAsync(this, "完成", "完成，重启 Codex 客户端查看");
            }
            else
            {
                var restore = await Task.Run(() => BackupService.RestoreBackup(home, info.Path));
                while (!restore.Success && restore.BlockingProcesses?.Count > 0)
                {
                    Log("还原被进程占用，发现 " + restore.BlockingProcesses.Count + " 个占用进程");
                    var holderList = string.Join("\n", restore.BlockingProcesses.Select(h => $"  · {h.ProcessName} (PID {h.Pid})"));
                    var doKill = await Dialogs.ConfirmAsync(this, "文件被进程占用",
                        $"以下进程持有 Codex 数据文件，导致还原失败:\n\n{holderList}\n\n" +
                        "强制结束这些进程并重试？(未保存的工作会丢)");
                    if (!doKill)
                    {
                        Log("用户取消强制结束");
                        await Dialogs.ErrorAsync(this, "还原失败", restore.Error ?? "未知错误",
                            copyableDetail: $"BlockingProcesses:\n{holderList}\n\n{restore.Error}");
                        return;
                    }
                    Log("正在强制结束占用进程...");
                    var kill = await Task.Run(() => ProcessKiller.KillAll(restore.BlockingProcesses));
                    Log($"已结束 {kill.Killed} 个 / 失败 {kill.Failed} 个");
                    if (kill.Failed > 0)
                    {
                        await Dialogs.ErrorAsync(this, "部分进程结束失败",
                            $"已成功结束 {kill.Killed} 个，但有 {kill.Failed} 个失败:\n" +
                            string.Join("\n", kill.Errors) +
                            "\n\n可能是需要管理员权限。请右键以管理员身份运行本程序后重试。");
                        return;
                    }
                    // 让 OS 释放文件句柄
                    await Task.Delay(500);
                    Log("重试还原...");
                    restore = await Task.Run(() => BackupService.RestoreBackup(home, info.Path));
                }
                if (!restore.Success)
                {
                    Log("还原失败: " + restore.Error);
                    await Dialogs.ErrorAsync(this, "还原失败", restore.Error ?? "未知错误",
                        copyableDetail: restore.Error);
                    return;
                }
                var parts = new List<string>();
                if (restore.SqliteRestored) parts.Add("数据库 ✓");
                if (restore.ConfigRestored) parts.Add("config.toml ✓");
                parts.Add($"{restore.RolloutFilesRestored} 个对话");
                Log("完成 — " + string.Join(" / ", parts));
                RestoredSomething = true;
                await Dialogs.InfoAsync(this, "完成",
                    "还原完成。\n  · " + string.Join("\n  · ", parts) +
                    "\n\n请彻底退出 Codex 客户端（含托盘）后重新打开，否则它内存里的缓存还是旧数据。");
            }
            LoadBackups();
        }
        catch (Exception ex)
        {
            Log($"出错：{ex.Message}");
            await Dialogs.ErrorAsync(this, "错误", ex.Message, copyableDetail: ex.ToString());
        }
    }
}
