using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexSyncWizard.Services;

public record ConversationInfo(
    string FilePath,
    string Provider,
    DateTime? Timestamp,
    string? Title,
    string? FirstUserMessage,
    string? Cwd,
    string? Model,
    int Turns,
    long FileSize,
    string? Source = null);

public static class SourceCategory
{
    public const string Desktop = "桌面/编辑器";
    public const string Cli = "CLI";
    public const string Exec = "exec";
    public const string Subagent = "子 agent";
    public const string Unknown = "其他";

    public static string Categorize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return Unknown;
        if (raw == "vscode") return Desktop;
        if (raw == "cli") return Cli;
        if (raw == "exec") return Exec;
        if (raw.StartsWith("{") && raw.Contains("subagent")) return Subagent;
        return Unknown;
    }
}

public static class ConversationBrowser
{
    /// <summary>
    /// 列出某 provider 下的所有对话。
    /// 策略：SQLite 优先 —— 能从 SQLite 拿到的就用，磁盘扫描只对 SQLite 漏掉的"孤儿 jsonl"做兜底。
    /// 这样 1000 个对话的常见场景从全文扫描（几 GB IO）变成只读 SQLite + 若干个孤儿首行（毫秒级）。
    /// </summary>
    public static List<ConversationInfo> ListByProvider(string codexHome, string provider)
    {
        var fromSqlite = QuerySqlite(codexHome, provider);

        // 收集 SQLite 里已知的 rollout_path，扫盘时跳过这些
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in fromSqlite)
            if (!string.IsNullOrEmpty(c.FilePath)) knownPaths.Add(c.FilePath);

        // 只扫 SQLite 没覆盖到的 jsonl（孤儿）—— 多线程并行读首行
        var orphans = ScanJsonlOrphans(codexHome, provider, knownPaths);

        var byPath = new Dictionary<string, ConversationInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in fromSqlite)
            if (!string.IsNullOrEmpty(c.FilePath)) byPath[c.FilePath] = c;
        foreach (var c in orphans)
            if (!string.IsNullOrEmpty(c.FilePath)) byPath[c.FilePath] = c;

        return byPath.Values.OrderByDescending(c => c.Timestamp ?? DateTime.MinValue).ToList();
    }

    public static bool IncludeInternalSources = false;

    private static List<ConversationInfo> QuerySqlite(string codexHome, string provider)
    {
        var result = new List<ConversationInfo>();
        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (!File.Exists(sqlitePath)) return result;

        try
        {
            using var conn = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            if (IncludeInternalSources)
                cmd.CommandText = @"SELECT rollout_path, title, first_user_message, cwd, model, created_at_ms, source
                                    FROM threads WHERE model_provider = @p
                                    ORDER BY created_at_ms DESC";
            else
                cmd.CommandText = @"SELECT rollout_path, title, first_user_message, cwd, model, created_at_ms, source
                                    FROM threads
                                    WHERE model_provider = @p
                                      AND (source IS NULL OR (source != 'exec' AND source NOT LIKE '%subagent%'))
                                    ORDER BY created_at_ms DESC";
            cmd.Parameters.AddWithValue("@p", provider);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var path = r.IsDBNull(0) ? "" : r.GetString(0);
                var title = r.IsDBNull(1) ? null : r.GetString(1);
                var firstMsg = r.IsDBNull(2) ? null : r.GetString(2);
                var cwd = r.IsDBNull(3) ? null : r.GetString(3);
                var model = r.IsDBNull(4) ? null : r.GetString(4);
                var ts = r.IsDBNull(5) ? (long?)null : r.GetInt64(5);
                var source = r.IsDBNull(6) ? null : r.GetString(6);
                DateTime? dt = ts == null ? null : DateTimeOffset.FromUnixTimeMilliseconds(ts.Value).LocalDateTime;
                long size = 0;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    try { size = new FileInfo(path).Length; } catch { }
                result.Add(new ConversationInfo(path, provider, dt, title, firstMsg, cwd, model, 0, size, source));
            }
        }
        catch { }

        return result;
    }

    private static List<ConversationInfo> ScanJsonlOrphans(string codexHome, string provider, HashSet<string> knownPaths)
    {
        var orphanFiles = new List<string>();
        foreach (var sub in new[] { "sessions", "archived_sessions" })
        {
            var dir = Path.Combine(codexHome, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            {
                if (!knownPaths.Contains(f)) orphanFiles.Add(f);
            }
        }
        if (orphanFiles.Count == 0) return new List<ConversationInfo>();

        // 并行只读首行（headOnly），避免全文扫描
        var bag = new System.Collections.Concurrent.ConcurrentBag<ConversationInfo>();
        Parallel.ForEach(orphanFiles, f =>
        {
            var info = TryReadFromFile(f, headOnly: true);
            if (info == null || info.Provider != provider) return;
            if (!IncludeInternalSources)
            {
                var cat = SourceCategory.Categorize(info.Source);
                if (cat == SourceCategory.Exec || cat == SourceCategory.Subagent) return;
            }
            bag.Add(info);
        });
        return bag.ToList();
    }

    public static ConversationInfo? TryReadFromFile(string filePath, bool headOnly = false)
    {
        try
        {
            using var sr = new StreamReader(filePath);
            var first = sr.ReadLine();
            if (string.IsNullOrEmpty(first)) return null;

            string provider = "";
            DateTime? ts = null;
            string? cwd = null;
            string? firstMsg = null;
            string? jsonlSource = null;
            int turns = 0;

            using (var doc = JsonDocument.Parse(first))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) return null;
                if (t.GetString() != "session_meta") return null;
                if (!root.TryGetProperty("payload", out var p)) return null;
                if (p.TryGetProperty("model_provider", out var mp)) provider = mp.GetString() ?? "";
                if (p.TryGetProperty("timestamp", out var tsEl) && DateTime.TryParse(tsEl.GetString(), out var parsed)) ts = parsed;
                if (p.TryGetProperty("cwd", out var cwdEl)) cwd = cwdEl.GetString();
                if (p.TryGetProperty("source", out var srcEl)) jsonlSource = srcEl.GetString();
            }

            // headOnly: 只读首行就返回，跳过 turns 统计和 firstMsg 全文搜索（昂贵）
            if (!headOnly)
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var t)) continue;
                        var typeName = t.GetString();
                        if (typeName == "event_msg")
                        {
                            if (root.TryGetProperty("payload", out var p) &&
                                p.TryGetProperty("type", out var subType) &&
                                subType.GetString() == "task_started")
                                turns++;
                        }
                        else if (typeName == "response_item" && firstMsg == null)
                        {
                            if (root.TryGetProperty("payload", out var p) &&
                                p.TryGetProperty("type", out var rt) && rt.GetString() == "message" &&
                                p.TryGetProperty("role", out var role) && role.GetString() == "user" &&
                                p.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in content.EnumerateArray())
                                {
                                    if (c.TryGetProperty("type", out var ct) && ct.GetString() == "input_text" &&
                                        c.TryGetProperty("text", out var txt))
                                    {
                                        var s = txt.GetString();
                                        if (!string.IsNullOrWhiteSpace(s) && !s.StartsWith("<"))
                                        {
                                            firstMsg = s;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            var fi = new FileInfo(filePath);
            return new ConversationInfo(filePath, provider, ts, null, firstMsg, cwd, null, turns, fi.Length, jsonlSource);
        }
        catch
        {
            return null;
        }
    }
}
