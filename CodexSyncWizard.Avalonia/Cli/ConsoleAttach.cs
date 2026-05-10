using System.Runtime.InteropServices;

namespace CodexSyncWizard.Avalonia.Cli;

internal static class ConsoleAttach
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private const int ATTACH_PARENT_PROCESS = -1;

    public static void EnsureConsole()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                AllocConsole();

            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stderr);
        }
        catch { }
    }

    public static void Detach()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try { FreeConsole(); } catch { }
    }
}
