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
    /// 把 ProviderNotDefinedException 翻译成友好弹窗。
    /// </summary>
    public static async Task ShowProviderNotDefinedAsync(Window parent, ProviderNotDefinedException ex)
    {
        string msg;
        if (ex.CaseMismatches.Count > 0)
        {
            msg = $"大小写不一致。Codex 对 provider 名严格区分大小写。\n\n" +
                  $"你写的: 「{ex.ProviderName}」\n" +
                  $"config.toml 里实际是: 「{string.Join("」/「", ex.CaseMismatches)}」\n\n" +
                  "改成 config.toml 里一字不差的写法重试。";
        }
        else if (ex.AllDefined.Count > 0)
        {
            msg = $"「{ex.ProviderName}」在 config.toml 里没定义。\n\n" +
                  $"已定义的 provider: {string.Join(", ", ex.AllDefined.Take(8))}" +
                  (ex.AllDefined.Count > 8 ? " ..." : "") + "\n\n" +
                  $"要么改用上面已有的名字，要么在 config.toml 加上:\n" +
                  $"[model_providers.{ex.ProviderName}]";
        }
        else
        {
            msg = $"「{ex.ProviderName}」在 config.toml 里没定义，且 config.toml 里没有任何 [model_providers.X] 段。\n\n" +
                  "请先在 config.toml 配置 provider。";
        }
        await ErrorAsync(parent, "目标 provider 不存在", msg);
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
