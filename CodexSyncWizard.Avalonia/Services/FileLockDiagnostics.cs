using System.Runtime.InteropServices;
using System.Text;

namespace CodexSyncWizard.Services;

public record FileHolder(int Pid, string ProcessName);

/// <summary>
/// Windows-only: 用 Restart Manager (rstrtmgr.dll) 列出当前持有指定文件的进程。
/// 用来在文件被锁删不掉时告诉用户具体是哪个进程占用了。
/// 其他平台返回空列表。
/// </summary>
public static class FileLockDiagnostics
{
    public static List<FileHolder> GetProcessesLocking(params string[] paths)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new List<FileHolder>();
        try { return GetProcessesLockingWindows(paths); }
        catch { return new List<FileHolder>(); }
    }

    public static string FormatHolders(IList<FileHolder> holders)
    {
        if (holders.Count == 0) return "(无法检测到具体进程)";
        return string.Join("\n", holders.Select(h => $"  · {h.ProcessName} (PID {h.Pid})"));
    }

    // === Windows Restart Manager P/Invoke ===
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)] public string strServiceShortName;
        public uint ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded,
        ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    private const int ERROR_MORE_DATA = 234;

    private static List<FileHolder> GetProcessesLockingWindows(string[] paths)
    {
        var holders = new List<FileHolder>();
        var key = new StringBuilder(Guid.NewGuid().ToString());
        if (RmStartSession(out var session, 0, key) != 0) return holders;
        try
        {
            if (RmRegisterResources(session, (uint)paths.Length, paths, 0, null, 0, null) != 0) return holders;

            uint procInfoNeeded = 0;
            uint procInfo = 0;
            uint rebootReasons = 0;
            var rc = RmGetList(session, out procInfoNeeded, ref procInfo, null, ref rebootReasons);
            if (procInfoNeeded == 0) return holders;

            // 扩容重新拿
            procInfo = procInfoNeeded;
            var processInfo = new RM_PROCESS_INFO[procInfoNeeded];
            rc = RmGetList(session, out procInfoNeeded, ref procInfo, processInfo, ref rebootReasons);
            if (rc != 0) return holders;

            for (int i = 0; i < procInfo; i++)
            {
                holders.Add(new FileHolder(
                    processInfo[i].Process.dwProcessId,
                    processInfo[i].strAppName ?? "(unknown)"));
            }
        }
        finally
        {
            RmEndSession(session);
        }
        return holders;
    }
}
