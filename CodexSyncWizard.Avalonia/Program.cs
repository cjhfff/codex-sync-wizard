using Avalonia;
using CodexSyncWizard.Avalonia.Cli;

namespace CodexSyncWizard.Avalonia;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
            return CliRunner.Run(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
