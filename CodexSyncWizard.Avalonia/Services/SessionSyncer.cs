using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace CodexSyncWizard.Services;

public enum SyncMode
{
    MergeToTarget,
    DeleteOthers,
    KeepOthers
}

public record SyncResult(
    int RolloutFilesSynced,
    int SqliteRowsSynced,
    string BackupPath,
    bool Cancelled = false,
    string? SqliteError = null);

public record PreviewResult(List<string> FilesToChange, int SqliteRowsToChange, Dictionary<string, int> CurrentDistribution);

public class SyncCancelledException : OperationCanceledException
{
    public SyncCancelledException() : base("用户取消") { }
}

public class SqliteLockedException : Exception
{
    public SqliteLockedException()
        : base("数据库被占用 — 请先关闭 Codex 客户端再试") { }
}

public class ProviderNotDefinedException : Exception
{
    public string ProviderName { get; }
    public List<string> CaseMismatches { get; }
    public List<string> AllDefined { get; }
    /// <summary>
    /// true 表示大小写错（同名但大小写不一致）—— 永远是硬错误，迁过去 Codex 续聊会 provider not found。
    /// false 表示根本没定义 —— 软错误，cc-switch 用户切回那个 provider 时也合法，可允许用户绕过。
    /// </summary>
    public bool IsCaseMismatch { get; }
    public ProviderNotDefinedException(string providerName, List<string> caseMismatches, List<string> allDefined)
        : base($"目标 provider「{providerName}」在 config.toml 里没定义")
    {
        ProviderName = providerName;
        CaseMismatches = caseMismatches;
        AllDefined = allDefined;
        IsCaseMismatch = caseMismatches.Count > 0;
    }
}

public static class SessionSyncer
{
    public static bool IsCodexLikelyRunning(string codexHome)
    {
        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (!File.Exists(sqlitePath)) return false;
        if (File.Exists(sqlitePath + "-wal") || File.Exists(sqlitePath + "-shm"))
            return TestSqliteWritable(sqlitePath) == false;
        return TestSqliteWritable(sqlitePath) == false;
    }

    private static bool TestSqliteWritable(string sqlitePath)
    {
        try
        {
            using var conn = SqliteConn.Open(sqlitePath, "ReadWrite");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "BEGIN IMMEDIATE; ROLLBACK;";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 校验目标 provider 是否在 config.toml 里定义。
    /// - 严格匹配 → OK
    /// - 大小写不一致 → 永远抛 (Codex 续聊会 provider not found，这种基本是手抖)
    /// - 完全没定义 → 取决于 allowMissingProvider
    ///     默认 false：抛 ProviderNotDefinedException (IsCaseMismatch=false)，UI 上弹软警告让用户决定
    ///     true：放行 (cc-switch 用户场景 — 把对话迁到 cc-switch 预设里的某个旧 provider，切回去就生效)
    /// </summary>
    public static void ValidateTargetProvider(string codexHome, string targetProvider, bool allowMissingProvider = false)
    {
        var defined = ConfigService.ListDefinedProviders(codexHome);
        if (defined.Count == 0) return; // config.toml 不存在或没定义任何 provider，跳过校验
        if (defined.Contains(targetProvider)) return; // 严格匹配，OK

        var caseMismatches = defined
            .Where(p => string.Equals(p, targetProvider, StringComparison.OrdinalIgnoreCase))
            .ToList();
        // 大小写错 → 总是抛 (即使 allowMissingProvider)
        if (caseMismatches.Count > 0)
            throw new ProviderNotDefinedException(targetProvider, caseMismatches, defined);
        // 完全没定义 → 看 allow
        if (!allowMissingProvider)
            throw new ProviderNotDefinedException(targetProvider, caseMismatches, defined);
    }

    public static SyncResult Sync(string codexHome, string targetProvider, bool updateConfig,
        SyncMode mode = SyncMode.MergeToTarget,
        IProgress<string>? progress = null, CancellationToken ct = default,
        bool allowMissingProvider = false)
    {
        if (IsCodexLikelyRunning(codexHome))
            throw new SqliteLockedException();

        ValidateTargetProvider(codexHome, targetProvider, allowMissingProvider);

        progress?.Report("正在备份...");
        var backupPath = BackupService.CreateBackup(codexHome);
        ct.ThrowIfCancellationRequested();

        int rolloutCount = 0;
        int sqliteCount = 0;
        string? sqliteError = null;

        if (mode == SyncMode.MergeToTarget)
        {
            progress?.Report("正在合并到目标渠道...");
            rolloutCount += SyncRolloutDir(Path.Combine(codexHome, "sessions"), targetProvider, ct);
            rolloutCount += SyncRolloutDir(Path.Combine(codexHome, "archived_sessions"), targetProvider, ct);

            progress?.Report("正在更新数据库...");
            sqliteCount = SyncSqlite(codexHome, targetProvider, out sqliteError);
        }
        else if (mode == SyncMode.DeleteOthers)
        {
            progress?.Report("正在删除非目标渠道的对话文件...");
            rolloutCount += DeleteOtherProviderRollouts(Path.Combine(codexHome, "sessions"), targetProvider, ct);
            rolloutCount += DeleteOtherProviderRollouts(Path.Combine(codexHome, "archived_sessions"), targetProvider, ct);

            progress?.Report("正在删除数据库中非目标渠道的记录...");
            sqliteCount = DeleteOtherProviderSqlite(codexHome, targetProvider, out sqliteError);
        }
        else
        {
            progress?.Report("已选「保留」模式，对话不动...");
        }
        ct.ThrowIfCancellationRequested();

        if (updateConfig)
        {
            progress?.Report("正在更新配置文件...");
            ConfigService.WriteProvider(codexHome, targetProvider);
        }

        BackupService.PruneBackups(codexHome);

        progress?.Report(mode == SyncMode.MergeToTarget ? "同步完成!" : "清理完成!");
        return new SyncResult(rolloutCount, sqliteCount, backupPath, false, sqliteError);
    }

    private static int DeleteOtherProviderRollouts(string dir, string targetProvider, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return 0;
        var files = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories).ToArray();
        int count = 0;
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct }, file =>
        {
            try
            {
                using (var sr = new StreamReader(file))
                {
                    var first = sr.ReadLine();
                    if (string.IsNullOrEmpty(first)) return;
                    using var doc = JsonDocument.Parse(first);
                    if (!doc.RootElement.TryGetProperty("type", out var t)) return;
                    if (t.GetString() != "session_meta") return;
                    if (!doc.RootElement.TryGetProperty("payload", out var p)) return;
                    if (!p.TryGetProperty("model_provider", out var mp)) return;
                    if (mp.GetString() == targetProvider) return;
                }
                File.Delete(file);
                Interlocked.Increment(ref count);
            }
            catch { }
        });
        return count;
    }

    private static int DeleteOtherProviderSqlite(string codexHome, string targetProvider, out string? error)
    {
        error = null;
        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (!File.Exists(sqlitePath)) return 0;

        try
        {
            using var conn = SqliteConn.Open(sqlitePath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM threads WHERE model_provider != @target";
            cmd.Parameters.AddWithValue("@target", targetProvider);
            var count = cmd.ExecuteNonQuery();
            CheckpointWal(conn);
            return count;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return 0;
        }
    }

    private static void CheckpointWal(SqliteConnection conn)
    {
        // 把 WAL 落到主库，避免 Codex Desktop 重启时读旧值
        try
        {
            using var ck = conn.CreateCommand();
            ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            ck.ExecuteNonQuery();
        }
        catch { /* checkpoint 失败不致命，正常 close 时 SQLite 也会尝试 */ }
    }

    private static int SyncRolloutDir(string dir, string targetProvider, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return 0;
        var files = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories).ToArray();
        int count = 0;
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct }, file =>
        {
            if (SyncRolloutFile(file, targetProvider))
                Interlocked.Increment(ref count);
        });
        return count;
    }

    private static bool SyncRolloutFile(string filePath, string targetProvider)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            if (lines.Length == 0) return false;

            var node = JsonNode.Parse(lines[0]);
            if (node == null) return false;

            var type = node["type"]?.GetValue<string>();
            if (type != "session_meta") return false;

            var currentProvider = node["payload"]?["model_provider"]?.GetValue<string>();
            if (currentProvider == targetProvider) return false;

            node["payload"]!["model_provider"] = targetProvider;
            lines[0] = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllLines(filePath, lines);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static PreviewResult Preview(string codexHome, string targetProvider)
    {
        var files = new List<string>();
        var dist = new Dictionary<string, int>();
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
                    if (!doc.RootElement.TryGetProperty("type", out var t)) continue;
                    if (t.GetString() != "session_meta") continue;
                    if (!doc.RootElement.TryGetProperty("payload", out var p)) continue;
                    if (!p.TryGetProperty("model_provider", out var mp)) continue;
                    var current = mp.GetString() ?? "";
                    dist[current] = dist.GetValueOrDefault(current) + 1;
                    if (current != targetProvider) files.Add(f);
                }
                catch { }
            }
        }

        int sqliteRows = 0;
        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (File.Exists(sqlitePath))
        {
            try
            {
                using var conn = SqliteConn.Open(sqlitePath, "ReadOnly");
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM threads WHERE model_provider != @target";
                cmd.Parameters.AddWithValue("@target", targetProvider);
                sqliteRows = Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch { }
        }

        return new PreviewResult(files, sqliteRows, dist);
    }

    public static SyncResult DeleteSpecificFiles(string codexHome, IList<string> filePaths,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (IsCodexLikelyRunning(codexHome))
            throw new SqliteLockedException();

        progress?.Report("正在备份...");
        var backupPath = BackupService.CreateBackup(codexHome);
        ct.ThrowIfCancellationRequested();

        progress?.Report($"正在删除 {filePaths.Count} 个对话...");
        int deleted = 0;
        var threadIds = new ConcurrentBag<string>();

        Parallel.ForEach(filePaths, new ParallelOptions { CancellationToken = ct }, fp =>
        {
            var id = ReadThreadId(fp);
            if (!string.IsNullOrEmpty(id)) threadIds.Add(id);
            try
            {
                if (File.Exists(fp))
                {
                    File.Delete(fp);
                    Interlocked.Increment(ref deleted);
                }
            }
            catch { }
        });

        progress?.Report("正在删除数据库记录...");
        int sqliteCount = DeleteSqliteByIds(codexHome, threadIds.ToList(), out var sqliteError);

        BackupService.PruneBackups(codexHome);
        progress?.Report("删除完成!");
        return new SyncResult(deleted, sqliteCount, backupPath, false, sqliteError);
    }

    private static string? ReadThreadId(string filePath)
    {
        try
        {
            using var sr = new StreamReader(filePath);
            var first = sr.ReadLine();
            if (string.IsNullOrEmpty(first)) return null;
            using var doc = JsonDocument.Parse(first);
            if (!doc.RootElement.TryGetProperty("payload", out var p)) return null;
            if (!p.TryGetProperty("id", out var idEl)) return null;
            return idEl.GetString();
        }
        catch { return null; }
    }

    private static int DeleteSqliteByIds(string codexHome, IList<string> threadIds, out string? error)
    {
        error = null;
        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (!File.Exists(sqlitePath) || threadIds.Count == 0) return 0;
        try
        {
            using var conn = SqliteConn.Open(sqlitePath);
            using var tx = conn.BeginTransaction();
            int deleted = 0;
            foreach (var id in threadIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM threads WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                deleted += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            CheckpointWal(conn);
            return deleted;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return 0;
        }
    }

    public static SyncResult SyncSpecificFiles(string codexHome, IList<string> filePaths,
        string targetProvider, IProgress<string>? progress = null, CancellationToken ct = default,
        bool allowMissingProvider = false)
    {
        if (IsCodexLikelyRunning(codexHome))
            throw new SqliteLockedException();

        ValidateTargetProvider(codexHome, targetProvider, allowMissingProvider);

        progress?.Report("正在备份...");
        var backupPath = BackupService.CreateBackup(codexHome);
        ct.ThrowIfCancellationRequested();

        progress?.Report($"正在迁移 {filePaths.Count} 个对话到「{targetProvider}」...");
        int rolloutCount = 0;
        var threadIds = new ConcurrentBag<string>();

        Parallel.ForEach(filePaths, new ParallelOptions { CancellationToken = ct }, fp =>
        {
            var id = SyncSingleFile(fp, targetProvider);
            if (!string.IsNullOrEmpty(id))
            {
                Interlocked.Increment(ref rolloutCount);
                threadIds.Add(id);
            }
        });

        progress?.Report("正在更新数据库...");
        int sqliteCount = UpdateSqliteByIds(codexHome, threadIds.ToList(), targetProvider, out var sqliteError);

        BackupService.PruneBackups(codexHome);

        progress?.Report("迁移完成!");
        return new SyncResult(rolloutCount, sqliteCount, backupPath, false, sqliteError);
    }

    private static string? SyncSingleFile(string filePath, string targetProvider)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var lines = File.ReadAllLines(filePath);
            if (lines.Length == 0) return null;

            var node = JsonNode.Parse(lines[0]);
            if (node == null) return null;
            if (node["type"]?.GetValue<string>() != "session_meta") return null;

            var payload = node["payload"];
            if (payload == null) return null;

            var current = payload["model_provider"]?.GetValue<string>();
            var threadId = payload["id"]?.GetValue<string>();

            if (current == targetProvider) return threadId;

            payload["model_provider"] = targetProvider;
            lines[0] = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllLines(filePath, lines);
            return threadId;
        }
        catch
        {
            return null;
        }
    }

    private static int UpdateSqliteByIds(string codexHome, IList<string> threadIds, string targetProvider, out string? error)
    {
        error = null;
        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (!File.Exists(sqlitePath) || threadIds.Count == 0) return 0;

        try
        {
            using var conn = SqliteConn.Open(sqlitePath);
            using var tx = conn.BeginTransaction();
            int updated = 0;
            foreach (var id in threadIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE threads SET model_provider = @p WHERE id = @id AND model_provider != @p";
                cmd.Parameters.AddWithValue("@p", targetProvider);
                cmd.Parameters.AddWithValue("@id", id);
                updated += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            CheckpointWal(conn);
            return updated;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return 0;
        }
    }

    private static int SyncSqlite(string codexHome, string targetProvider, out string? error)
    {
        error = null;
        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (!File.Exists(sqlitePath)) return 0;

        try
        {
            using var conn = SqliteConn.Open(sqlitePath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE threads SET model_provider = @target WHERE model_provider != @target";
            cmd.Parameters.AddWithValue("@target", targetProvider);
            var count = cmd.ExecuteNonQuery();
            CheckpointWal(conn);
            return count;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return 0;
        }
    }
}
