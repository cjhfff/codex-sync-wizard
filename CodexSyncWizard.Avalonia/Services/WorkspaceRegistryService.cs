using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexSyncWizard.Services;

public static class WorkspaceRegistryService
{
    public static string GetStatePath(string codexHome)
        => Path.Combine(codexHome, ".codex-global-state.json");

    public static bool IsCodexDesktopRunning()
    {
        try
        {
            var procs = Process.GetProcesses();
            foreach (var p in procs)
            {
                try
                {
                    var name = p.ProcessName;
                    if (string.Equals(name, "Codex", StringComparison.OrdinalIgnoreCase) && p.WorkingSet64 > 50 * 1024 * 1024)
                        return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    public static List<string> GetWorkspaces(string codexHome)
    {
        var list = new List<string>();
        var path = GetStatePath(codexHome);
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("electron-saved-workspace-roots", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
        }
        catch { }
        return list;
    }

    public static string Normalize(string p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        const string longPrefix = @"\\?\";
        if (p.StartsWith(longPrefix)) p = p.Substring(longPrefix.Length);
        p = p.Replace('/', '\\').TrimEnd('\\');
        return p;
    }

    public static bool IsRegistered(string codexHome, string cwd)
    {
        var target = Normalize(cwd);
        if (string.IsNullOrEmpty(target)) return false;
        var registered = GetWorkspaces(codexHome).Select(Normalize);
        return registered.Any(r =>
            string.Equals(r, target, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    public enum AddResult
    {
        Added,
        AlreadyExists,
        CodexRunning,
        FileMissing,
        Error
    }

    public static AddResult AddWorkspace(string codexHome, string cwd, out string? errorMsg)
    {
        errorMsg = null;
        var path = GetStatePath(codexHome);
        if (!File.Exists(path))
        {
            errorMsg = "未找到 .codex-global-state.json";
            return AddResult.FileMissing;
        }

        if (IsCodexDesktopRunning())
            return AddResult.CodexRunning;

        var clean = Normalize(cwd);
        if (string.IsNullOrEmpty(clean))
        {
            errorMsg = "路径为空";
            return AddResult.Error;
        }

        try
        {
            var content = File.ReadAllText(path);
            var node = JsonNode.Parse(content)!;

            var roots = node["electron-saved-workspace-roots"] as JsonArray ?? new JsonArray();
            var ordered = node["project-order"] as JsonArray ?? new JsonArray();

            bool exists = false;
            foreach (var item in roots)
            {
                var s = Normalize(item?.GetValue<string>() ?? "");
                if (string.Equals(s, clean, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }
            if (exists) return AddResult.AlreadyExists;

            var bak = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(path, bak);

            var newRoots = new JsonArray();
            foreach (var r in roots) newRoots.Add(r?.DeepClone());
            newRoots.Add(clean);
            node["electron-saved-workspace-roots"] = newRoots;

            var newOrdered = new JsonArray();
            foreach (var r in ordered) newOrdered.Add(r?.DeepClone());
            newOrdered.Add(clean);
            node["project-order"] = newOrdered;

            File.WriteAllText(path, node.ToJsonString());
            var sibling = path + ".bak";
            if (File.Exists(sibling)) File.WriteAllText(sibling, node.ToJsonString());

            return AddResult.Added;
        }
        catch (Exception ex)
        {
            errorMsg = ex.Message;
            return AddResult.Error;
        }
    }
}
