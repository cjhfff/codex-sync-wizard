using System.Diagnostics;

namespace CodexSyncWizard.Services;

public record KillResult(int Killed, int Failed, List<string> Errors);

public static class ProcessKiller
{
    /// <summary>
    /// 强制结束指定 PID 列表。整个进程树都杀。每个杀完最多等 2s 让 OS 释放文件句柄。
    /// 返回杀掉的数量 + 失败列表（PID 不存在不算失败）。
    /// </summary>
    public static KillResult KillAll(IList<FileHolder> holders)
    {
        int ok = 0, fail = 0;
        var errs = new List<string>();

        foreach (var h in holders)
        {
            try
            {
                Process p;
                try { p = Process.GetProcessById(h.Pid); }
                catch (ArgumentException) { ok++; continue; /* 进程已退出，当作成功 */ }

                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                errs.Add($"{h.ProcessName}(PID {h.Pid}): {ex.Message}");
            }
        }
        return new KillResult(ok, fail, errs);
    }
}
