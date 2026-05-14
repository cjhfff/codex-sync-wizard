using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexSyncWizard.Services;

public record ProviderInfo(string Name, int RolloutCount, int SqliteCount)
{
    public int TotalCount => RolloutCount + SqliteCount;
}

public record ScanResult(
    Dictionary<string, ProviderInfo> Providers,
    int TotalRolloutFiles,
    int TotalArchivedFiles,
    int TotalSqliteThreads,
    List<string> Warnings);

public static class ProviderScanner
{
    /// <summary>
    /// 默认 false：只统计用户能直接看到的对话（CLI + Desktop），
    /// 不算 codex exec "..." 一次性请求 和 子 agent 自动派生的 thread。
    /// </summary>
    public static ScanResult Scan(string codexHome, bool includeInternalSources = false)
    {
        var rolloutProviders = new Dictionary<string, int>();
        var sqliteProviders = new Dictionary<string, int>();
        var warnings = new List<string>();
        int totalRollout = 0;
        int totalArchived = 0;
        int totalSqliteThreads = 0;

        // 1. 拿 SQLite 中各 thread.id → source 的映射，用来判断每个 jsonl 是不是要算
        var sourceById = new Dictionary<string, string>();
        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (File.Exists(sqlitePath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly");
                conn.Open();
                using var cmdMap = conn.CreateCommand();
                cmdMap.CommandText = "SELECT id, source FROM threads";
                using (var rr = cmdMap.ExecuteReader())
                    while (rr.Read())
                        sourceById[rr.GetString(0)] = rr.IsDBNull(1) ? "" : rr.GetString(1);

                using var cmd = conn.CreateCommand();
                if (includeInternalSources)
                    cmd.CommandText = "SELECT model_provider, COUNT(*) FROM threads GROUP BY model_provider";
                else
                    cmd.CommandText = @"SELECT model_provider, COUNT(*) FROM threads
                                        WHERE source IS NULL OR (source != 'exec' AND source NOT LIKE '%subagent%')
                                        GROUP BY model_provider";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var p = reader.GetString(0);
                    var count = reader.GetInt32(1);
                    sqliteProviders[p] = count;
                    totalSqliteThreads += count;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"SQLite 读取失败: {ex.Message}");
            }
        }
        else
        {
            warnings.Add("未找到 state_5.sqlite");
        }

        bool ShouldCount(string filePath, out string? provider)
        {
            provider = null;
            try
            {
                using var sr = new StreamReader(filePath);
                var first = sr.ReadLine();
                if (string.IsNullOrEmpty(first)) return false;
                using var doc = JsonDocument.Parse(first);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "session_meta") return false;
                if (!root.TryGetProperty("payload", out var payload)) return false;
                if (payload.TryGetProperty("model_provider", out var mp))
                    provider = mp.GetString();
                if (!includeInternalSources)
                {
                    string? id = payload.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (id != null && sourceById.TryGetValue(id, out var src))
                    {
                        if (src == "exec" || src.Contains("subagent")) return false;
                    }
                }
                return provider != null;
            }
            catch { return false; }
        }

        // 并行扫两个目录的 jsonl 首行 —— IO bound，多线程提速 5-10x
        var rolloutCounter = new ConcurrentDictionary<string, int>();
        int totalRolloutAtomic = 0;
        int totalArchivedAtomic = 0;

        void ScanDir(string dir, bool isArchived)
        {
            if (!Directory.Exists(dir)) return;
            var files = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories).ToArray();
            Parallel.ForEach(files, file =>
            {
                if (ShouldCount(file, out var provider))
                {
                    if (isArchived) Interlocked.Increment(ref totalArchivedAtomic);
                    else Interlocked.Increment(ref totalRolloutAtomic);
                    if (provider != null)
                        rolloutCounter.AddOrUpdate(provider, 1, (_, v) => v + 1);
                }
            });
        }

        ScanDir(Path.Combine(codexHome, "sessions"), false);
        ScanDir(Path.Combine(codexHome, "archived_sessions"), true);

        totalRollout = totalRolloutAtomic;
        totalArchived = totalArchivedAtomic;
        foreach (var kv in rolloutCounter) rolloutProviders[kv.Key] = kv.Value;

        // 合并：对话里出现过的 provider + config.toml 已定义的 provider
        // （后者即使 0 条也要保留，否则迁完一次 provider 卡片就消失，用户以为没了）
        var definedProviders = ConfigService.ListDefinedProviders(codexHome);
        var allProviderNames = rolloutProviders.Keys
            .Union(sqliteProviders.Keys)
            .Union(definedProviders)
            .Distinct()
            .ToList();

        var providers = new Dictionary<string, ProviderInfo>();
        foreach (var name in allProviderNames)
        {
            providers[name] = new ProviderInfo(
                name,
                rolloutProviders.GetValueOrDefault(name),
                sqliteProviders.GetValueOrDefault(name));
        }

        return new ScanResult(providers, totalRollout, totalArchived, totalSqliteThreads, warnings);
    }
}
