using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CodexSyncWizard.Services;

namespace CodexSyncWizard.Avalonia;

public partial class PreviewWindow : Window
{
    public PreviewWindow() { InitializeComponent(); }

    public PreviewWindow(string codexHome, string targetProvider, PreviewResult preview) : this()
    {
        HeaderLabel.Text = $"将归到「{targetProvider}」 — 共 {preview.FilesToChange.Count} 个对话 / {preview.SqliteRowsToChange} 条数据库记录";

        var distLines = new List<string>();
        foreach (var kv in preview.CurrentDistribution.OrderByDescending(k => k.Value))
            distLines.Add($"{kv.Key} ×{kv.Value}");
        SubLabel.Text = "当前分布：" + (distLines.Count > 0 ? string.Join(",  ", distLines) : "（无）");

        var sb = new StringBuilder();
        if (preview.FilesToChange.Count == 0)
        {
            sb.AppendLine("（没有需要修改的对话文件）");
        }
        else
        {
            foreach (var f in preview.FilesToChange.OrderBy(x => x))
            {
                var rel = f.StartsWith(codexHome, StringComparison.OrdinalIgnoreCase)
                    ? f.Substring(codexHome.Length).TrimStart('\\', '/')
                    : f;
                sb.AppendLine(rel);
            }
        }
        FileList.Text = sb.ToString();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
