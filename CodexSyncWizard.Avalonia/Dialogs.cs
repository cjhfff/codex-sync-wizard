using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;

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
