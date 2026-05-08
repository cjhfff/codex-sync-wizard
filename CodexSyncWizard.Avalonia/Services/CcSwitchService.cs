using Microsoft.Data.Sqlite;

namespace CodexSyncWizard.Services;

public record CcSwitchInfo(string CurrentProviderId, string DbPath);

public static class CcSwitchService
{
    public static string GetDbPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cc-switch", "cc-switch.db");
    }

    public static bool IsInstalled()
    {
        return File.Exists(GetDbPath());
    }

    public static CcSwitchInfo? GetCurrentCodexProvider()
    {
        var path = GetDbPath();
        if (!File.Exists(path)) return null;

        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM providers WHERE app_type = 'codex' AND is_current = 1 LIMIT 1";
            var result = cmd.ExecuteScalar() as string;
            return result == null ? null : new CcSwitchInfo(result, path);
        }
        catch
        {
            return null;
        }
    }
}
