using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexSyncWizard.Services;

public enum BackupFormat
{
    Unknown,
    Compact,
    Full
}

public record LegacyBackupInfo(
    string Path,
    string Name,
    DateTime CreatedAt,
    BackupFormat Format,
    string? TargetProvider,
    int ChangedFileCount,
    Dictionary<string, int> OriginalProviders);

public record LegacyRestoreResult(
    int FilesRestored,
    int SqliteRowsUpdated,
    Dictionary<string, int> ResultDistribution);

public static class LegacyBackupService
{
    private const string BackupSubDir = "backups_state/provider-sync";

    public static List<LegacyBackupInfo> ListAll(string codexHome)
    {
        var dir = Path.Combine(codexHome, BackupSubDir);
        if (!Directory.Exists(dir)) return new();

        var result = new List<LegacyBackupInfo>();
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var info = Inspect(sub);
            if (info != null) result.Add(info);
        }
        return result.OrderByDescending(b => b.CreatedAt).ToList();
    }

    private static LegacyBackupInfo? Inspect(string backupPath)
    {
        var name = Path.GetFileName(backupPath);
        var compactMeta = Path.Combine(backupPath, "metadata.json");
        var compactBody = Path.Combine(backupPath, "session-meta-backup.json");

        if (File.Exists(compactMeta) && File.Exists(compactBody))
        {
            try
            {
                using var metaDoc = JsonDocument.Parse(File.ReadAllText(compactMeta));
                var meta = metaDoc.RootElement;
                var target = meta.TryGetProperty("targetProvider", out var t) ? t.GetString() : null;
                var changed = meta.TryGetProperty("changedSessionFiles", out var c) ? c.GetInt32() : 0;
                var createdStr = meta.TryGetProperty("createdAt", out var d) ? d.GetString() : null;
                DateTime.TryParse(createdStr, out var created);
                if (created == default) created = Directory.GetCreationTime(backupPath);

                var origDist = ParseOriginalProviders(compactBody);

                return new LegacyBackupInfo(backupPath, name, created, BackupFormat.Compact,
                    target, changed, origDist);
            }
            catch
            {
                return null;
            }
        }

        var hasSessions = Directory.Exists(Path.Combine(backupPath, "sessions")) ||
                          Directory.Exists(Path.Combine(backupPath, "archived_sessions")) ||
                          File.Exists(Path.Combine(backupPath, "state_5.sqlite"));
        if (hasSessions)
        {
            return new LegacyBackupInfo(backupPath, name,
                Directory.GetCreationTime(backupPath),
                BackupFormat.Full, null, 0, new Dictionary<string, int>());
        }

        return null;
    }

    private static Dictionary<string, int> ParseOriginalProviders(string compactBodyPath)
    {
        var counts = new Dictionary<string, int>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(compactBodyPath));
            if (!doc.RootElement.TryGetProperty("files", out var files)) return counts;
            foreach (var f in files.EnumerateArray())
            {
                if (!f.TryGetProperty("originalFirstLine", out var line)) continue;
                var s = line.GetString();
                if (string.IsNullOrEmpty(s)) continue;
                try
                {
                    using var inner = JsonDocument.Parse(s);
                    if (inner.RootElement.TryGetProperty("payload", out var payload) &&
                        payload.TryGetProperty("model_provider", out var mp))
                    {
                        var name = mp.GetString() ?? "(null)";
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }
                }
                catch { }
            }
        }
        catch { }
        return counts;
    }

    public static LegacyRestoreResult RestoreCompact(string codexHome, string backupPath,
        IProgress<string>? progress = null)
    {
        progress?.Report($"读取备份 {Path.GetFileName(backupPath)}...");
        var compactBody = Path.Combine(backupPath, "session-meta-backup.json");
        if (!File.Exists(compactBody))
            throw new FileNotFoundException("找不到 session-meta-backup.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(compactBody));
        if (!doc.RootElement.TryGetProperty("files", out var files))
            throw new InvalidDataException("备份格式不正确");

        int restored = 0;
        var threadProviderMap = new Dictionary<string, string>();

        foreach (var f in files.EnumerateArray())
        {
            var path = f.TryGetProperty("path", out var p) ? p.GetString() : null;
            var origLine = f.TryGetProperty("originalFirstLine", out var l) ? l.GetString() : null;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(origLine)) continue;
            if (!File.Exists(path)) continue;

            try
            {
                var lines = File.ReadAllLines(path).ToList();
                if (lines.Count == 0) continue;
                lines[0] = origLine!;
                File.WriteAllLines(path, lines);
                restored++;

                using var inner = JsonDocument.Parse(origLine!);
                if (inner.RootElement.TryGetProperty("payload", out var payload))
                {
                    string? id = payload.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    string? prov = payload.TryGetProperty("model_provider", out var mpEl) ? mpEl.GetString() : null;
                    if (id != null && prov != null) threadProviderMap[id] = prov;
                }
            }
            catch { }
        }

        progress?.Report($"已还原 {restored} 个对话文件");
        progress?.Report("更新数据库...");

        int sqliteUpdated = UpdateSqliteFromMap(codexHome, threadProviderMap);
        progress?.Report($"数据库已更新 {sqliteUpdated} 条记录");

        var dist = ScanCurrentDistribution(codexHome);
        return new LegacyRestoreResult(restored, sqliteUpdated, dist);
    }

    public static LegacyRestoreResult RestoreOriginalDistribution(string codexHome,
        IProgress<string>? progress = null)
    {
        var allBackups = ListAll(codexHome)
            .Where(b => b.Format == BackupFormat.Compact && b.ChangedFileCount > 0)
            .OrderByDescending(b => b.CreatedAt)
            .ToList();

        if (allBackups.Count == 0)
            throw new InvalidOperationException("没有可还原的紧凑格式备份");

        progress?.Report($"找到 {allBackups.Count} 份备份，按时间从新到旧依次叠加还原...");

        int totalRestored = 0;
        foreach (var b in allBackups)
        {
            progress?.Report($"还原 {b.Name}（同步前目标={b.TargetProvider}，影响 {b.ChangedFileCount} 个文件）");
            var r = RestoreCompact(codexHome, b.Path, null);
            totalRestored += r.FilesRestored;
        }

        progress?.Report("重建数据库 model_provider 映射...");
        int sqliteUpdated = RebuildSqliteFromAllRollouts(codexHome);

        var dist = ScanCurrentDistribution(codexHome);
        return new LegacyRestoreResult(totalRestored, sqliteUpdated, dist);
    }

    private static int UpdateSqliteFromMap(string codexHome, Dictionary<string, string> threadProviderMap)
    {
        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (!File.Exists(sqlitePath) || threadProviderMap.Count == 0) return 0;

        try
        {
            using var conn = new SqliteConnection($"Data Source={sqlitePath}");
            conn.Open();
            using var tx = conn.BeginTransaction();
            int updated = 0;
            foreach (var kv in threadProviderMap)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE threads SET model_provider = @p WHERE id = @id AND model_provider != @p";
                cmd.Parameters.AddWithValue("@p", kv.Value);
                cmd.Parameters.AddWithValue("@id", kv.Key);
                updated += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return updated;
        }
        catch
        {
            return 0;
        }
    }

    private static int RebuildSqliteFromAllRollouts(string codexHome)
    {
        var map = new Dictionary<string, string>();
        foreach (var sub in new[] { "sessions", "archived_sessions" })
        {
            var dir = Path.Combine(codexHome, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    using var sr = new StreamReader(f);
                    var first = sr.ReadLine();
                    if (string.IsNullOrEmpty(first)) continue;
                    using var doc = JsonDocument.Parse(first);
                    if (doc.RootElement.TryGetProperty("payload", out var payload))
                    {
                        string? id = payload.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        string? prov = payload.TryGetProperty("model_provider", out var mpEl) ? mpEl.GetString() : null;
                        if (id != null && prov != null) map[id] = prov;
                    }
                }
                catch { }
            }
        }
        return UpdateSqliteFromMap(codexHome, map);
    }

    public static Dictionary<string, int> ScanCurrentDistribution(string codexHome)
    {
        var counts = new Dictionary<string, int>();
        foreach (var sub in new[] { "sessions", "archived_sessions" })
        {
            var dir = Path.Combine(codexHome, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    using var sr = new StreamReader(f);
                    var first = sr.ReadLine();
                    if (string.IsNullOrEmpty(first)) continue;
                    using var doc = JsonDocument.Parse(first);
                    if (doc.RootElement.TryGetProperty("payload", out var payload) &&
                        payload.TryGetProperty("model_provider", out var mp))
                    {
                        var name = mp.GetString() ?? "(null)";
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                    }
                }
                catch { }
            }
        }
        return counts;
    }
}
