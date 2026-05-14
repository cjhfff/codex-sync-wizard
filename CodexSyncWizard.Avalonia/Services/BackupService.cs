using Microsoft.Data.Sqlite;

namespace CodexSyncWizard.Services;

public record RestoreResult(
    bool Success,
    int RolloutFilesRestored,
    bool SqliteRestored,
    bool ConfigRestored,
    string? Error);

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
            using var conn = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadWrite");
            conn.Open();
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

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.jsonl", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(codexHome, file);
            var destPath = Path.Combine(backupDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath);
        }
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

                // 必须先删除 -wal/-shm，否则 SQLite 会把旧 WAL 重放到刚还原的文件上覆盖回去。
                // 删不掉就 hard fail —— 否则用户以为还原成功，下次 Codex 启动数据立刻被覆盖回旧值。
                if (File.Exists(walPath))
                {
                    try { File.Delete(walPath); }
                    catch (Exception ex)
                    {
                        return new RestoreResult(false, 0, false, false,
                            $"无法删除旧的 SQLite WAL 文件 ({walPath}): {ex.Message}\n\n" +
                            "通常是 Codex 仍在运行或文件被某个进程持有。" +
                            "请打开任务管理器搜索 Codex / Electron 全部关掉后重试。");
                    }
                }
                if (File.Exists(shmPath))
                {
                    try { File.Delete(shmPath); }
                    catch (Exception ex)
                    {
                        return new RestoreResult(false, 0, false, false,
                            $"无法删除旧的 SQLite SHM 文件 ({shmPath}): {ex.Message}");
                    }
                }

                try { File.Copy(sqliteBackup, current, overwrite: true); }
                catch (Exception ex)
                {
                    return new RestoreResult(false, 0, false, false,
                        $"还原数据库失败: {ex.Message}\n\n" +
                        "可能是文件被 Codex 占用。请彻底关闭 Codex 后重试。");
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

    private static int RestoreRolloutFiles(string codexHome, string backupDir, string subDir)
    {
        var backupSubDir = Path.Combine(backupDir, subDir);
        if (!Directory.Exists(backupSubDir)) return 0;

        int count = 0;
        foreach (var file in Directory.EnumerateFiles(backupSubDir, "*.jsonl", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(backupDir, file);
            var destPath = Path.Combine(codexHome, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
            count++;
        }
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
