using Microsoft.Data.Sqlite;

namespace CodexSyncWizard.Services;

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

    public static void RestoreBackup(string codexHome, string backupDir)
    {
        if (SessionSyncer.IsCodexLikelyRunning(codexHome))
            throw new SqliteLockedException();

        var sqliteBackup = Path.Combine(backupDir, "state_5.sqlite");
        if (File.Exists(sqliteBackup))
        {
            var current = Path.Combine(codexHome, "state_5.sqlite");
            // 必须先删除 -wal/-shm，否则 SQLite 会把旧 WAL 重放到刚还原的文件上覆盖回去
            try { File.Delete(current + "-wal"); } catch { }
            try { File.Delete(current + "-shm"); } catch { }
            File.Copy(sqliteBackup, current, overwrite: true);
        }

        var configBackup = Path.Combine(backupDir, "config.toml");
        if (File.Exists(configBackup))
            File.Copy(configBackup, Path.Combine(codexHome, "config.toml"), overwrite: true);

        RestoreRolloutFiles(codexHome, backupDir, "sessions");
        RestoreRolloutFiles(codexHome, backupDir, "archived_sessions");
    }

    private static void RestoreRolloutFiles(string codexHome, string backupDir, string subDir)
    {
        var backupSubDir = Path.Combine(backupDir, subDir);
        if (!Directory.Exists(backupSubDir)) return;

        foreach (var file in Directory.EnumerateFiles(backupSubDir, "*.jsonl", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(backupDir, file);
            var destPath = Path.Combine(codexHome, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
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
