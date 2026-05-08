using System.Text.RegularExpressions;

namespace CodexSyncWizard.Services;

public static class ConfigService
{
    public static string? ReadProvider(string codexHome)
    {
        var path = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(path)) return null;

        var content = File.ReadAllText(path);
        var match = Regex.Match(content, @"^model_provider\s*=\s*""([^""]+)""", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static List<string> ListDefinedProviders(string codexHome)
    {
        var path = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(path)) return new List<string>();
        var content = File.ReadAllText(path);
        var matches = Regex.Matches(content, @"^\[model_providers\.([^\]]+)\]", RegexOptions.Multiline);
        return matches.Select(m => m.Groups[1].Value.Trim('"')).Distinct().ToList();
    }

    public static bool IsProviderDefined(string codexHome, string providerName)
    {
        return ListDefinedProviders(codexHome).Contains(providerName);
    }

    public static void WriteProvider(string codexHome, string provider)
    {
        var path = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(path)) return;

        var content = File.ReadAllText(path);
        var replaced = Regex.Replace(
            content,
            @"^(model_provider\s*=\s*"")[^""]+("")",
            $"${{1}}{provider}${{2}}",
            RegexOptions.Multiline);

        File.WriteAllText(path, replaced);
    }
}
