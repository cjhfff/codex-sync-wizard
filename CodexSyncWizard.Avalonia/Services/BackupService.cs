using Microsoft.Data.Sqlite;

namespace CodexSyncWizard.Services;

public record RestoreResult(
    bool Success,
    int RolloutFilesRestored,
    bool SqliteRestored,
    bool ConfigRestored,
    string? Error,
    List<FileHolder>? BlockingProcesses = null);

public static class BackupService
{
    private const string BackupSubDir = "backups_state/provider-sync";

    public static string CreateBackup(string codexHome)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(codexHome, BackupSubDir, timestamp);
        Directory.CreateDirectory(backupDir);

        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (File.Exists(sqlitePath))
        {
            // 先把 WAL 合并到主文件（不然 -wal 里的新事务备份不到）
            CheckpointSqlite(sqlitePath);
            File.Copy(sqlitePath, Path.Combine(backupDir, "state_5.sqlite"));
        }

        var configPath = Path.Combine(codexHome, "config.toml");
        if (File.Exists(configPath))
            File.Copy(configPath, Path.Combine(backupDir, "config.toml"));

        BackupRolloutFiles(codexHome, "sessions", backupDir);
        BackupRolloutFiles(codexHome, "archived_sessions", backupDir);

        return backupDir;
    }

    private static void CheckpointSqlite(string sqlitePath)
    {
        try
        {
            using var conn = SqliteConn.Open(sqlitePath, "ReadWrite");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch { /* Codex 锁着也无所谓，最坏漏几条 WAL，仍可还原大部分 */ }
    }

    private static void BackupRolloutFiles(string codexHome, string subDir, string backupDir)
    {
        var sourceDir = Path.Combine(codexHome, subDir);
        if (!Directory.Exists(sourceDir)) return;

        var files = Directory.EnumerateFiles(sourceDir, "*.jsonl", SearchOption.AllDirectories).ToArray();
        // 先把目标子目录全部建好，避免并行 CreateDirectory 互相竞争
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(codexHome, file);
            var destPath = Path.Combine(backupDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        }
        Parallel.ForEach(files, file =>
        {
            var relativePath = Path.GetRelativePath(codexHome, file);
            var destPath = Path.Combine(backupDir, relativePath);
            File.Copy(file, destPath);
        });
    }

    public static List<string> ListBackups(string codexHome)
    {
        var dir = Path.Combine(codexHome, BackupSubDir);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetDirectories(dir)
            .OrderByDescending(d => d)
            .ToList();
    }

    public static RestoreResult RestoreBackup(string codexHome, string backupDir)
    {
        // 兜底: 清空 ADO.NET 内部 SqliteConnection 池，避免我们自己的扫描/迁移留下的池化连接占着文件。
        // (常规连接已用 SqliteConn.Open 强制 Pooling=False；这一行保护历史第三方代码留下的池连接。)
        SqliteConn.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (SessionSyncer.IsCodexLikelyRunning(codexHome))
            return new RestoreResult(false, 0, false, false,
                "Codex 客户端正在运行 — 请彻底关闭（含托盘 / Helper 进程）后再还原。否则旧数据库的 WAL 会覆盖刚还原的内容。");

        bool sqliteOk = false;
        bool configOk = false;
        int rolloutCount = 0;

        try
        {
            var sqliteBackup = Path.Combine(backupDir, "state_5.sqlite");
            if (File.Exists(sqliteBackup))
            {
                var current = Path.Combine(codexHome, "state_5.sqlite");
                var walPath = current + "-wal";
                var shmPath = current + "-shm";

                // 必须先处理 -wal/-shm，否则 SQLite 会把旧 WAL 重放到刚还原的文件上覆盖回去。
                // 三步策略：1) 试 Delete; 2) Delete 失败试 Move 到 .bak; 3) 都不行报告占用进程让用户处理。
                if (File.Exists(walPath))
                {
                    var err = TryRemoveLockableFile(walPath, out var blocking);
                    if (err != null) return new RestoreResult(false, 0, false, false, err, blocking);
                }
                if (File.Exists(shmPath))
                {
                    var err = TryRemoveLockableFile(shmPath, out var blocking);
                    if (err != null) return new RestoreResult(false, 0, false, false, err, blocking);
                }

                try { File.Copy(sqliteBackup, current, overwrite: true); }
                catch (Exception ex)
                {
                    var holders = FileLockDiagnostics.GetProcessesLocking(current);
                    return new RestoreResult(false, 0, false, false,
                        $"还原数据库失败: {ex.Message}\n\n" +
                        $"持有这个文件的进程:\n{FileLockDiagnostics.FormatHolders(holders)}",
                        holders);
                }
                sqliteOk = true;
            }

            var configBackup = Path.Combine(backupDir, "config.toml");
            if (File.Exists(configBackup))
            {
                File.Copy(configBackup, Path.Combine(codexHome, "config.toml"), overwrite: true);
                configOk = true;
            }

            rolloutCount += RestoreRolloutFiles(codexHome, backupDir, "sessions");
            rolloutCount += RestoreRolloutFiles(codexHome, backupDir, "archived_sessions");

            return new RestoreResult(true, rolloutCount, sqliteOk, configOk, null);
        }
        catch (Exception ex)
        {
            return new RestoreResult(false, rolloutCount, sqliteOk, configOk, ex.Message);
        }
    }

    /// <summary>
    /// 删除一个被某进程锁住的文件。
    /// 1. 先试 Delete；
    /// 2. 失败则试 Move 到 .bak 文件名（Move 在 Windows 上对共享锁容忍度高些）；
    /// 3. 都失败：返回包含占用进程列表的错误信息（Windows 用 Restart Manager 检测）。
    /// 返回 null 表示成功 / 文件已不存在；返回 string 表示失败原因。
    /// </summary>
    private static string? TryRemoveLockableFile(string path, out List<FileHolder>? blocking)
    {
        blocking = null;
        if (!File.Exists(path)) return null;
        try { File.Delete(path); return null; }
        catch (Exception delEx)
        {
            // Fallback 1: 试 Move 到带时间戳的 .bak —— 比 Delete 容忍度高
            var fallback = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            try
            {
                File.Move(path, fallback);
                return null;
            }
            catch (Exception moveEx)
            {
                blocking = FileLockDiagnostics.GetProcessesLocking(path);
                var processInfo = blocking.Count > 0
                    ? $"\n\n持有这个文件的进程:\n{FileLockDiagnostics.FormatHolders(blocking)}"
                    : "";
                return $"无法删除被锁定的文件:\n  {path}\n\n" +
                       $"Delete 错误: {delEx.Message}\n" +
                       $"Move 错误: {moveEx.Message}" + processInfo;
            }
        }
    }

    private static int RestoreRolloutFiles(string codexHome, string backupDir, string subDir)
    {
        var backupSubDir = Path.Combine(backupDir, subDir);
        if (!Directory.Exists(backupSubDir)) return 0;

        var files = Directory.EnumerateFiles(backupSubDir, "*.jsonl", SearchOption.AllDirectories).ToArray();
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(backupDir, file);
            var destPath = Path.Combine(codexHome, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        }
        int count = 0;
        Parallel.ForEach(files, file =>
        {
            var relativePath = Path.GetRelativePath(backupDir, file);
            var destPath = Path.Combine(codexHome, relativePath);
            File.Copy(file, destPath, overwrite: true);
            Interlocked.Increment(ref count);
        });
        return count;
    }

    public static void PruneBackups(string codexHome, int keepCount = 2)
    {
        var backups = ListBackups(codexHome);
        foreach (var old in backups.Skip(keepCount))
        {
            try { Directory.Delete(old, recursive: true); } catch { }
        }
    }
}
