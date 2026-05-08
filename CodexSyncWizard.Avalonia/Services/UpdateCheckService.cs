using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace CodexSyncWizard.Services;

public record UpdateInfo(string LatestVersion, string DownloadUrl, string CurrentVersion);

public static class UpdateCheckService
{
    public const string GitHubOwner = "cjhfff";
    public const string GitHubRepo = "codex-sync-wizard";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var current = GetCurrentVersion();
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodexSyncWizard", current));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await Client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : "";

            if (string.IsNullOrEmpty(tag)) return null;
            var latest = NormalizeVersion(tag);

            if (CompareVersions(latest, current) > 0)
                return new UpdateInfo(latest, htmlUrl ?? "", current);

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static string GetCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(ver))
        {
            var plus = ver.IndexOf('+');
            if (plus > 0) ver = ver.Substring(0, plus);
            return ver;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string NormalizeVersion(string s)
    {
        s = s.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s.Substring(1);
        return s;
    }

    private static int CompareVersions(string a, string b)
    {
        int[] Parse(string v)
        {
            var parts = v.Split('.', '-');
            var nums = new List<int>();
            foreach (var p in parts)
            {
                if (int.TryParse(p, out var n)) nums.Add(n);
                else break;
            }
            while (nums.Count < 3) nums.Add(0);
            return nums.ToArray();
        }
        var x = Parse(a);
        var y = Parse(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var xi = i < x.Length ? x[i] : 0;
            var yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }
}
