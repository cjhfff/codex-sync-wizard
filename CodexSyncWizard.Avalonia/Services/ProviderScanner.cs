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
    public static ScanResult Scan(string codexHome)
    {
        var rolloutProviders = new Dictionary<string, int>();
        var sqliteProviders = new Dictionary<string, int>();
        var warnings = new List<string>();
        int totalRollout = 0;
        int totalArchived = 0;
        int totalSqliteThreads = 0;

        var sessionsDir = Path.Combine(codexHome, "sessions");
        if (Directory.Exists(sessionsDir))
        {
            foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.jsonl", SearchOption.AllDirectories))
            {
                totalRollout++;
                var provider = ReadProviderFromRollout(file);
                if (provider != null)
                    rolloutProviders[provider] = rolloutProviders.GetValueOrDefault(provider) + 1;
            }
        }

        var archivedDir = Path.Combine(codexHome, "archived_sessions");
        if (Directory.Exists(archivedDir))
        {
            foreach (var file in Directory.EnumerateFiles(archivedDir, "*.jsonl", SearchOption.AllDirectories))
            {
                totalArchived++;
                var provider = ReadProviderFromRollout(file);
                if (provider != null)
                    rolloutProviders[provider] = rolloutProviders.GetValueOrDefault(provider) + 1;
            }
        }

        var sqlitePath = Path.Combine(codexHome, "state_5.sqlite");
        if (File.Exists(sqlitePath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT model_provider, COUNT(*) FROM threads GROUP BY model_provider";
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

        var allProviderNames = rolloutProviders.Keys.Union(sqliteProviders.Keys).Distinct().ToList();
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

    private static string? ReadProviderFromRollout(string filePath)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            var firstLine = reader.ReadLine();
            if (string.IsNullOrEmpty(firstLine)) return null;

            using var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "session_meta")
            {
                if (root.TryGetProperty("payload", out var payload) &&
                    payload.TryGetProperty("model_provider", out var mp))
                {
                    return mp.GetString();
                }
            }
        }
        catch { }
        return null;
    }
}
