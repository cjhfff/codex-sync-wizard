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
            File.Copy(sqlitePath, Path.Combine(backupDir, "state_5.sqlite"));

        var configPath = Path.Combine(codexHome, "config.toml");
        if (File.Exists(configPath))
            File.Copy(configPath, Path.Combine(backupDir, "config.toml"));

        BackupRolloutFiles(codexHome, "sessions", backupDir);
        BackupRolloutFiles(codexHome, "archived_sessions", backupDir);

        return backupDir;
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
        var sqliteBackup = Path.Combine(backupDir, "state_5.sqlite");
        if (File.Exists(sqliteBackup))
            File.Copy(sqliteBackup, Path.Combine(codexHome, "state_5.sqlite"), overwrite: true);

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

    public static void PruneBackups(string codexHome, int keepCount = 5)
    {
        var backups = ListBackups(codexHome);
        foreach (var old in backups.Skip(keepCount))
        {
            try { Directory.Delete(old, recursive: true); } catch { }
        }
    }
}
