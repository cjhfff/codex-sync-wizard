namespace CodexSyncWizard.Services;

public static class CodexHomeService
{
    public static string GetDefaultPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public static bool Exists(string codexHome)
    {
        return Directory.Exists(codexHome);
    }

    public static bool HasSessions(string codexHome)
    {
        var sessionsDir = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessionsDir)) return false;
        return Directory.EnumerateFiles(sessionsDir, "*.jsonl", SearchOption.AllDirectories).Any();
    }

    public static bool HasSqlite(string codexHome)
    {
        return File.Exists(Path.Combine(codexHome, "state_5.sqlite"));
    }

    public static bool HasConfig(string codexHome)
    {
        return File.Exists(Path.Combine(codexHome, "config.toml"));
    }
}
