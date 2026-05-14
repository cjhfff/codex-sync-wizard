using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using CodexSyncWizard.Services;

namespace CodexSyncWizard.Avalonia;

public record DialogButton(string Text, bool IsPrimary = false, Action? OnClick = null);

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
    /// 把 ProviderNotDefinedException 翻译成友好弹窗。
    /// </summary>
    public static async Task ShowProviderNotDefinedAsync(Window parent, ProviderNotDefinedException ex)
    {
        var msg = $"目标 provider「{ex.ProviderName}」在 config.toml 里没定义。\n";
        if (ex.CaseMismatches.Count > 0)
        {
            msg += "\n大小写不一致 — Codex 对 provider 名是大小写敏感的。你写的是:\n" +
                   $"  「{ex.ProviderName}」\n" +
                   "config.toml 里实际是:\n" +
                   string.Join("\n", ex.CaseMismatches.Select(p => $"  「{p}」")) +
                   "\n\n请把目标名改成上面一字不差的写法，或修 config.toml。";
        }
        else if (ex.AllDefined.Count > 0)
        {
            msg += "\nconfig.toml 里只定义了:\n" +
                   string.Join("\n", ex.AllDefined.Take(10).Select(p => "  · " + p)) +
                   "\n\n请在 config.toml 加上 [model_providers." + ex.ProviderName + "] 段，或改用上面已有的名字。";
        }
        await InfoAsync(parent, "目标 provider 不存在", msg);
    }

    /// <summary>
    /// SyncResult 出现 SqliteError 或 "jsonl 改了但数据库 0 条" 时弹警告。
    /// 返回是否报了警告（true 表示半成功；调用方可决定要不要继续后续 UI）。
    /// </summary>
    public static async Task<bool> WarnIfPartialSyncAsync(Window parent, SyncResult r)
    {
        if (!string.IsNullOrEmpty(r.SqliteError))
        {
            await InfoAsync(parent, "迁移半成功 — 数据库未更新",
                $"对话文件改了 {r.RolloutFilesSynced} 个，但数据库写入失败。\n\n" +
                $"原因: {r.SqliteError}\n\n" +
                "Codex Desktop 列表主要看数据库 — 它可能仍看不到本次迁移结果。\n\n" +
                "建议步骤:\n" +
                "  1. 彻底退出 Codex (含右下角托盘 / Helper 进程)\n" +
                "  2. 「高级 → 备份列表」还原刚才那一份\n" +
                "  3. 重试迁移");
            return true;
        }
        if (r.RolloutFilesSynced > 0 && r.SqliteRowsSynced == 0)
        {
            await InfoAsync(parent, "提醒 — 数据库无对应记录更新",
                $"对话文件改了 {r.RolloutFilesSynced} 个，但数据库 0 条更新。\n\n" +
                "可能原因:\n" +
                "  · jsonl 里的对话 ID 在 SQLite 里找不到 (历史迁移过 / 手改过)\n" +
                "  · Codex 客户端仍在跑，写入虽然没异常但被 WAL 隔离了\n\n" +
                "如果重启 Codex 后列表仍异常，请用「高级 → 备份列表」还原后重试。");
            return true;
        }
        return false;
    }

    public static async Task<int> ShowAsync(Window parent, string title, string message, DialogButton[] buttons)
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
            btn.Click += (_, _) =>
            {
                chosen = idx;
                b.OnClick?.Invoke();
                dlg.Close();
            };
            btnPanel.Children.Add(btn);
        }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(msg, 0);
        Grid.SetRow(btnPanel, 1);
        grid.Children.Add(msg);
        grid.Children.Add(btnPanel);

        dlg.Content = grid;

        await dlg.ShowDialog(parent);
        return chosen;
    }
}
