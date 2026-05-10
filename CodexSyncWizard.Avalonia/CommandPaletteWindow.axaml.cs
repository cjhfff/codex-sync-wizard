using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CodexSyncWizard.Avalonia.Cli;

namespace CodexSyncWizard.Avalonia;

public partial class CommandPaletteWindow : Window
{
    private readonly List<string> _history = new();
    private int _historyIdx = -1;
    private static readonly string[] QuickCommands = new[]
    {
        "scan",
        "providers",
        "workspaces",
        "list --provider OpenAI",
        "list --provider custom",
        "migrate --all-to custom --yes",
        "smart-restore",
        "restore --list",
        "version",
        "help"
    };

    public CommandPaletteWindow()
    {
        InitializeComponent();
        BuildQuickButtons();
        CmdInput.KeyDown += OnInputKeyDown;
        Opened += (_, _) => CmdInput.Focus();
        AppendOutput("$ codex-sync help    # 试一下\n");
    }

    private void BuildQuickButtons()
    {
        foreach (var cmd in QuickCommands)
        {
            var btn = new Button
            {
                Content = cmd,
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 11,
                Padding = new Thickness(8, 3),
                Background = (IBrush)Application.Current!.FindResource("PanelBrush")!,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            btn.Click += (_, _) =>
            {
                CmdInput.Text = cmd;
                CmdInput.CaretIndex = cmd.Length;
                CmdInput.Focus();
            };
            QuickPanel.Children.Add(btn);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = ExecuteAsync(CmdInput.Text);
        }
        else if (e.Key == Key.Up)
        {
            if (_history.Count == 0) return;
            if (_historyIdx == -1) _historyIdx = _history.Count - 1;
            else if (_historyIdx > 0) _historyIdx--;
            CmdInput.Text = _history[_historyIdx];
            CmdInput.CaretIndex = CmdInput.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (_historyIdx == -1) return;
            if (_historyIdx < _history.Count - 1)
            {
                _historyIdx++;
                CmdInput.Text = _history[_historyIdx];
            }
            else
            {
                _historyIdx = -1;
                CmdInput.Text = "";
            }
            CmdInput.CaretIndex = CmdInput.Text.Length;
            e.Handled = true;
        }
    }

    private async void OnExecute(object? sender, RoutedEventArgs e)
    {
        await ExecuteAsync(CmdInput.Text);
    }

    private async Task ExecuteAsync(string? raw)
    {
        var input = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(input)) return;

        if (_history.Count == 0 || _history[^1] != input)
            _history.Add(input);
        _historyIdx = -1;

        AppendOutput($"$ codex-sync {input}\n");
        CmdInput.Text = "";

        var args = CliRunner.SplitCommandLine(input);
        var sw = new StringWriter();
        int code = 0;
        await Task.Run(() =>
        {
            code = CliRunner.RunInProcess(args, sw);
        });

        var output = sw.ToString();
        AppendOutput(output);
        if (!output.EndsWith("\n")) AppendOutput("\n");
        AppendOutput($"[exit {code}]\n\n");
        OutScroll.ScrollToEnd();
        CmdInput.Focus();
    }

    private void AppendOutput(string s)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OutBox.Text = (OutBox.Text ?? "") + s;
            OutScroll.ScrollToEnd();
        });
    }

    private void OnClear(object? sender, RoutedEventArgs e) => OutBox.Text = "";
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
