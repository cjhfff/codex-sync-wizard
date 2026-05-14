using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using CodexSyncWizard.Services;

namespace CodexSyncWizard.Avalonia;

public record DialogButton(string Text, bool IsPrimary = false, Func<Task>? OnClickAsync = null, bool CloseAfter = true);

public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(Window parent, string title, string message)
    {
        return await ShowAsync(parent, title, message, new[]
        {
            new DialogButton("取消"),
            new DialogButton("确定", IsPrimary: true)
        }) == 1;
    }

    public static async Task InfoAsync(Window parent, string title, string message)
    {
        await ShowAsync(parent, title, message, new[]
        {
            new DialogButton("确定", IsPrimary: true)
        });
    }

    /// <summary>
    /// 红色风格的错误弹窗，带"复制错误"按钮。
    /// 客户报问题时让 ta 直接复制错误丢回来，省去手抄文字。
    /// </summary>
    public static async Task ErrorAsync(Window parent, string title, string message, string? copyableDetail = null)
    {
        var detailToCopy = copyableDetail ?? message;
        var buttons = new[]
        {
            new DialogButton("复制错误", CloseAfter: false, OnClickAsync: async () =>
            {
                try
                {
                    var clip = TopLevel.GetTopLevel(parent)?.Clipboard;
                    if (clip != null) await clip.SetTextAsync($"[{title}]\n{detailToCopy}");
                }
                catch { }
            }),
            new DialogButton("确定", IsPrimary: true)
        };
        await ShowAsync(parent, title, message, buttons, isError: true);
    }

    /// <summary>
    /// 大小写错误的硬错误弹窗 (无路可走，必须改名)。
    /// </summary>
    public static async Task ShowProviderCaseMismatchAsync(Window parent, ProviderNotDefinedException ex)
    {
        var msg = $"大小写不一致。Codex 对 provider 名严格区分大小写，迁过去续聊会报「provider not found」。\n\n" +
                  $"你写的: 「{ex.ProviderName}」\n" +
                  $"config.toml 里实际是: 「{string.Join("」/「", ex.CaseMismatches)}」\n\n" +
                  "改成 config.toml 里一字不差的写法重试。";
        await ErrorAsync(parent, "大小写不一致 — 必须改名", msg);
    }

    /// <summary>
    /// 目标 provider 在 config.toml 里没定义的软警告。用户点"继续迁移"会绕过校验。
    /// 适用场景: cc-switch 用户把对话迁到一个目前不在 config.toml 但在 cc-switch 其他预设里的 provider。
    /// 返回 true 表示用户选择继续。
    /// </summary>
    public static async Task<bool> ShowProviderMissingSoftWarnAsync(Window parent, ProviderNotDefinedException ex)
    {
        var msg = $"「{ex.ProviderName}」目前不在 config.toml 里。\n";
        if (ex.AllDefined.Count > 0)
            msg += $"\nconfig.toml 当前定义的: {string.Join(", ", ex.AllDefined.Take(8))}" +
                   (ex.AllDefined.Count > 8 ? " ..." : "") + "\n";
        msg += "\n如果你用 cc-switch 之类的工具切换 provider — 这是正常的，切回那个 provider 时就能用。\n\n" +
               "继续迁移？还是先去 config.toml 加 [model_providers." + ex.ProviderName + "] 段再来？";
        return await ConfirmAsync(parent, "目标 provider 不在当前 config.toml", msg);
    }

    /// <summary>
    /// SqliteLockedException 携带占用进程时，弹「强制结束并迁移」对话框，让用户选择是否杀进程后重试。
    /// 返回 true 表示用户选择强制结束 — 调用方应该 kill 这些进程后重试整个迁移。
    /// 返回 false 表示用户取消。
    /// </summary>
    public static async Task<bool> ShowCodexRunningWithKillAsync(Window parent, SqliteLockedException ex)
    {
        if (ex.BlockingProcesses.Count == 0)
        {
            await InfoAsync(parent, "Codex 客户端正在运行", ex.Message);
            return false;
        }
        var holderList = string.Join("\n", ex.BlockingProcesses.Select(h => $"  · {h.ProcessName} (PID {h.Pid})"));
        return await ConfirmAsync(parent, "Codex 客户端正在运行",
            $"以下进程正在使用 Codex 数据库，迁移会失败:\n\n{holderList}\n\n" +
            "强制结束这些进程并继续迁移？\n" +
            "(未保存的 Codex 对话会丢；迁移完后需要重新启动 Codex)");
    }

    /// <summary>
    /// 用 ProcessKiller 杀掉一组进程，结束失败时弹错误。
    /// 返回 true 表示全部杀成功，可以重试操作。
    /// </summary>
    public static async Task<bool> KillBlockersAsync(Window parent, List<FileHolder> blockers)
    {
        var kill = await Task.Run(() => ProcessKiller.KillAll(blockers));
        if (kill.Failed > 0)
        {
            await ErrorAsync(parent, "部分进程结束失败",
                $"已结束 {kill.Killed} 个，但有 {kill.Failed} 个失败:\n" +
                string.Join("\n", kill.Errors) +
                "\n\n可能是需要管理员权限。请右键以管理员身份运行本程序后重试。");
            return false;
        }
        // 让 OS 释放文件句柄
        await Task.Delay(500);
        return true;
    }

    /// <summary>
    /// 统一 catch ProviderNotDefinedException 的入口。
    /// - 大小写错: 弹硬错误，返回 false (不要重试)
    /// - 完全没定义: 弹软警告，返回 true 表示用户选择"继续迁移"应该重试 (传 allowMissingProvider: true)
    /// </summary>
    public static async Task<bool> HandleProviderNotDefinedAsync(Window parent, ProviderNotDefinedException ex)
    {
        if (ex.IsCaseMismatch)
        {
            await ShowProviderCaseMismatchAsync(parent, ex);
            return false;
        }
        return await ShowProviderMissingSoftWarnAsync(parent, ex);
    }

    /// <summary>
    /// SyncResult 出现 SqliteError 或 "jsonl 改了但数据库 0 条" 时弹警告。
    /// 返回是否报了警告（true 表示半成功；调用方可决定要不要继续后续 UI）。
    /// </summary>
    public static async Task<bool> WarnIfPartialSyncAsync(Window parent, SyncResult r)
    {
        if (!string.IsNullOrEmpty(r.SqliteError))
        {
            await ErrorAsync(parent, "迁移半成功 — 数据库未更新",
                $"对话文件改了 {r.RolloutFilesSynced} 个，数据库写入失败:\n{r.SqliteError}\n\n" +
                "Codex Desktop 看的是数据库 — 它可能仍看不到本次迁移。\n\n" +
                "建议: 彻底关 Codex (含托盘) → 「高级 → 备份列表」还原 → 重试。",
                copyableDetail: $"SqliteError: {r.SqliteError}\nRolloutFilesSynced: {r.RolloutFilesSynced}\nBackupPath: {r.BackupPath}");
            return true;
        }
        if (r.RolloutFilesSynced > 0 && r.SqliteRowsSynced == 0)
        {
            await InfoAsync(parent, "提醒",
                $"对话改了 {r.RolloutFilesSynced} 个，但数据库 0 条更新。\n\n" +
                "通常是 jsonl 的 ID 在 SQLite 里找不到 (历史迁移过 / 手改过)，或 Codex 仍在跑。\n" +
                "重启 Codex 后如果列表仍异常，用「高级 → 备份列表」还原后重试。");
            return true;
        }
        return false;
    }

    public static async Task<int> ShowAsync(Window parent, string title, string message, DialogButton[] buttons, bool isError = false)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 500,
            MinWidth = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.FindResource("BgBrush")!,
            ShowInTaskbar = false
        };

        // 顶部 accent bar — error 红色，普通对话框不显示
        var accentBar = new Border
        {
            Height = 4,
            Background = isError
                ? new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C))
                : Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var msg = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (IBrush)Application.Current!.FindResource("TextBrush")!,
            FontSize = 13,
            Margin = new Thickness(20, 20, 20, 12)
        };

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 16),
            Spacing = 8
        };

        int chosen = -1;
        for (int i = 0; i < buttons.Length; i++)
        {
            var idx = i;
            var b = buttons[i];
            var btn = new Button { Content = b.Text, MinWidth = 80 };
            if (b.IsPrimary) btn.Classes.Add("primary");
            btn.Click += async (_, _) =>
            {
                if (b.OnClickAsync != null)
                {
                    try { await b.OnClickAsync(); } catch { }
                }
                if (b.CloseAfter)
                {
                    chosen = idx;
                    dlg.Close();
                }
                else
                {
                    // "复制错误"按钮提示反馈
                    btn.Content = "已复制 ✓";
                    await Task.Delay(1200);
                    btn.Content = b.Text;
                }
            };
            btnPanel.Children.Add(btn);
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(accentBar, 0);
        Grid.SetRow(msg, 1);
        Grid.SetRow(btnPanel, 2);
        grid.Children.Add(accentBar);
        grid.Children.Add(msg);
        grid.Children.Add(btnPanel);

        dlg.Content = grid;

        await dlg.ShowDialog(parent);
        return chosen;
    }
}
