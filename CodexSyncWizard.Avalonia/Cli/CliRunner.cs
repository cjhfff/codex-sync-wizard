using CodexSyncWizard.Services;

namespace CodexSyncWizard.Avalonia.Cli;

public static class CliRunner
{
    public static int Run(string[] args)
    {
        ConsoleAttach.EnsureConsole();
        try
        {
            return Dispatch(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var cmd = args[0];
        var sub = args.Skip(1).ToArray();
        var opts = ParseOptions(sub);
        var home = opts.GetValueOrDefault("codex-home") ?? CodexHomeService.GetDefaultPath();
        if (!Directory.Exists(home))
        {
            Console.Error.WriteLine($"Codex 目录不存在: {home}");
            return 2;
        }

        return cmd switch
        {
            "scan" => CmdScan(home),
            "providers" => CmdProviders(home),
            "list" => CmdList(home, opts),
            "migrate" => CmdMigrate(home, opts),
            "delete" => CmdDelete(home, opts),
            "register-workspace" => CmdRegisterWorkspace(home, sub),
            "workspaces" => CmdWorkspaces(home),
            "set-default" => CmdSetDefault(home, sub),
            "restore" => CmdRestore(home, opts, sub),
            "smart-restore" => CmdSmartRestore(home),
            "version" or "--version" => CmdVersion(),
            _ => Unknown(cmd)
        };
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            var key = a.Substring(2);
            string value;
            var eq = key.IndexOf('=');
            if (eq > 0) { value = key.Substring(eq + 1); key = key.Substring(0, eq); }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { value = args[++i]; }
            else value = "true";
            d[key] = value;
        }
        return d;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令: {cmd}");
        Console.Error.WriteLine("用 'codex-sync help' 查看可用命令");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"CodexSyncWizard CLI

用法: codex-sync <命令> [选项]

命令:
  scan                                扫描 Codex 目录，按 provider 列对话数
  providers                           列出 config.toml 已定义的 provider
  list [--provider X] [--cwd Y]       列出对话（可按 provider/cwd 过滤）
  migrate --from X --to Y             把 provider X 的全部对话迁到 Y
  migrate --all-to Y                  所有对话归到 Y
  delete --provider X                 删除某 provider 全部对话（先备份）
  register-workspace <path>           把 path 加入 Codex 工作区列表
  workspaces                          列出 Codex 已注册的工作区
  set-default <provider>              改 config.toml 顶层 model_provider
  restore --list                      列出可还原的备份
  restore --apply <name>              还原某个备份
  smart-restore                       叠加还原 Dailin521 紧凑备份
  version                             显示版本

通用选项:
  --codex-home <path>                 覆盖默认的 ~/.codex
  --yes                               跳过交互确认（危险操作前用）

示例:
  codex-sync scan
  codex-sync migrate --from openai --to custom --yes
  codex-sync migrate --all-to custom
  codex-sync register-workspace D:\HermesAgent
  codex-sync set-default sub2api
");
    }

    private static int CmdScan(string home)
    {
        var scan = ProviderScanner.Scan(home);
        Console.WriteLine($"Codex 目录: {home}");
        Console.WriteLine($"对话文件: {scan.TotalRolloutFiles} 个 / 归档 {scan.TotalArchivedFiles} 个");
        Console.WriteLine($"数据库记录: {scan.TotalSqliteThreads} 条");
        Console.WriteLine($"渠道分布:");
        foreach (var p in scan.Providers.Values.OrderByDescending(p => p.TotalCount))
            Console.WriteLine($"  {p.Name,-15} 对话 {p.RolloutCount,4} + 数据库 {p.SqliteCount,4} = {p.TotalCount}");
        var current = ConfigService.ReadProvider(home);
        if (!string.IsNullOrEmpty(current))
            Console.WriteLine($"\n当前 config.toml 默认 provider: {current}");
        if (scan.Warnings.Count > 0)
        {
            Console.WriteLine("\n警告:");
            foreach (var w in scan.Warnings) Console.WriteLine("  " + w);
        }
        return 0;
    }

    private static int CmdProviders(string home)
    {
        var defined = ConfigService.ListDefinedProviders(home);
        Console.WriteLine($"config.toml 里定义的 provider ({defined.Count}):");
        foreach (var p in defined) Console.WriteLine("  " + p);
        var current = ConfigService.ReadProvider(home);
        Console.WriteLine($"\n当前默认: {current ?? "(无)"}");
        return 0;
    }

    private static int CmdList(string home, Dictionary<string, string> opts)
    {
        var provider = opts.GetValueOrDefault("provider");
        var cwdFilter = opts.GetValueOrDefault("cwd");
        var limit = int.TryParse(opts.GetValueOrDefault("limit"), out var n) ? n : 50;

        if (string.IsNullOrEmpty(provider))
        {
            Console.WriteLine("提示: --provider <name> 必填，否则用 'codex-sync scan' 看汇总");
            return 2;
        }

        var list = ConversationBrowser.ListByProvider(home, provider);
        if (!string.IsNullOrEmpty(cwdFilter))
        {
            var norm = WorkspaceRegistryService.Normalize(cwdFilter).ToLowerInvariant();
            list = list.Where(c =>
                !string.IsNullOrEmpty(c.Cwd) &&
                WorkspaceRegistryService.Normalize(c.Cwd).ToLowerInvariant().Contains(norm)
            ).ToList();
        }

        Console.WriteLine($"Provider {provider} 下共 {list.Count} 条对话");
        if (list.Count > limit) Console.WriteLine($"（只显示前 {limit} 条，--limit N 调整）");
        foreach (var c in list.Take(limit))
        {
            var t = (c.Title ?? c.FirstUserMessage ?? "").Replace("\n", " ");
            if (t.Length > 60) t = t.Substring(0, 60) + "...";
            var ts = c.Timestamp?.ToString("MM-dd HH:mm") ?? "        ";
            Console.WriteLine($"  {ts}  {t,-62}  {ShortName(c.Cwd)}");
        }
        return 0;
    }

    private static string ShortName(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "(无 cwd)";
        var s = WorkspaceRegistryService.Normalize(cwd).TrimEnd('\\', '/');
        var idx = Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\'));
        return idx >= 0 ? s.Substring(idx + 1) : s;
    }

    private static int CmdMigrate(string home, Dictionary<string, string> opts)
    {
        var from = opts.GetValueOrDefault("from");
        var to = opts.GetValueOrDefault("to") ?? opts.GetValueOrDefault("all-to");
        var allTo = opts.ContainsKey("all-to");
        var yes = opts.ContainsKey("yes");

        if (string.IsNullOrEmpty(to))
        {
            Console.Error.WriteLine("缺少 --to 或 --all-to");
            return 2;
        }
        if (!allTo && string.IsNullOrEmpty(from))
        {
            Console.Error.WriteLine("缺少 --from 参数（或用 --all-to <provider> 一锅端）");
            return 2;
        }

        var scan = ProviderScanner.Scan(home);
        List<string> sourcePaths = new();
        int sqliteToChange = 0;

        if (allTo)
        {
            foreach (var p in scan.Providers.Values)
            {
                if (p.Name == to) continue;
                var convs = ConversationBrowser.ListByProvider(home, p.Name);
                foreach (var c in convs)
                    if (!string.IsNullOrEmpty(c.FilePath)) sourcePaths.Add(c.FilePath);
                sqliteToChange += p.SqliteCount;
            }
        }
        else
        {
            if (!scan.Providers.ContainsKey(from!))
            {
                Console.Error.WriteLine($"找不到 provider: {from}");
                return 2;
            }
            var convs = ConversationBrowser.ListByProvider(home, from!);
            foreach (var c in convs)
                if (!string.IsNullOrEmpty(c.FilePath)) sourcePaths.Add(c.FilePath);
            sqliteToChange = scan.Providers[from!].SqliteCount;
        }

        Console.WriteLine($"将迁移到「{to}」:");
        Console.WriteLine($"  对话文件: {sourcePaths.Count}");
        Console.WriteLine($"  数据库记录: {sqliteToChange}");
        Console.WriteLine($"  操作前自动备份");

        if (sourcePaths.Count == 0 && sqliteToChange == 0)
        {
            Console.WriteLine("没有需要迁移的对话");
            return 0;
        }

        if (!yes)
        {
            Console.Write("确认执行? (y/N): ");
            var input = Console.ReadLine();
            if (input?.Trim().ToLower() != "y")
            {
                Console.WriteLine("已取消");
                return 0;
            }
        }

        var progress = new Progress<string>(s => Console.WriteLine("  " + s));
        try
        {
            var result = SessionSyncer.SyncSpecificFiles(home, sourcePaths, to!, progress);
            Console.WriteLine($"\n完成: 改 {result.RolloutFilesSynced} 个对话 / {result.SqliteRowsSynced} 条数据库记录");
            Console.WriteLine($"备份: {result.BackupPath}");
            return 0;
        }
        catch (SqliteLockedException)
        {
            Console.Error.WriteLine("数据库被占用 — 请先关闭 Codex 客户端再试");
            return 3;
        }
    }

    private static int CmdDelete(string home, Dictionary<string, string> opts)
    {
        var provider = opts.GetValueOrDefault("provider");
        var yes = opts.ContainsKey("yes");

        if (string.IsNullOrEmpty(provider))
        {
            Console.Error.WriteLine("缺少 --provider <name>");
            return 2;
        }

        var convs = ConversationBrowser.ListByProvider(home, provider);
        var paths = convs.Where(c => !string.IsNullOrEmpty(c.FilePath)).Select(c => c.FilePath!).ToList();

        Console.WriteLine($"将删除 provider「{provider}」下:");
        Console.WriteLine($"  对话文件: {paths.Count}");
        Console.WriteLine($"  操作前自动备份，可还原");

        if (paths.Count == 0)
        {
            Console.WriteLine("没有需要删除的对话");
            return 0;
        }

        if (!yes)
        {
            Console.Write("⚠ 永久删除? (y/N): ");
            if (Console.ReadLine()?.Trim().ToLower() != "y") { Console.WriteLine("已取消"); return 0; }
        }

        var progress = new Progress<string>(s => Console.WriteLine("  " + s));
        try
        {
            var result = SessionSyncer.DeleteSpecificFiles(home, paths, progress);
            Console.WriteLine($"\n完成: 删除 {result.RolloutFilesSynced} 个对话 / {result.SqliteRowsSynced} 条数据库记录");
            Console.WriteLine($"备份: {result.BackupPath}");
            return 0;
        }
        catch (SqliteLockedException)
        {
            Console.Error.WriteLine("数据库被占用 — 请先关闭 Codex 客户端再试");
            return 3;
        }
    }

    private static int CmdRegisterWorkspace(string home, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("用法: codex-sync register-workspace <path>");
            return 2;
        }
        var path = args[0];
        var result = WorkspaceRegistryService.AddWorkspace(home, path, out var err);
        switch (result)
        {
            case WorkspaceRegistryService.AddResult.Added: Console.WriteLine($"已加入: {path}"); return 0;
            case WorkspaceRegistryService.AddResult.AlreadyExists: Console.WriteLine($"已存在: {path}"); return 0;
            case WorkspaceRegistryService.AddResult.CodexRunning:
                Console.Error.WriteLine("Codex Desktop 正在运行，写入会被覆盖。请先彻底退出 Codex 再试。");
                return 3;
            default:
                Console.Error.WriteLine($"失败: {err}");
                return 1;
        }
    }

    private static int CmdWorkspaces(string home)
    {
        var ws = WorkspaceRegistryService.GetWorkspaces(home);
        Console.WriteLine($"Codex 已注册的 workspace ({ws.Count}):");
        foreach (var w in ws) Console.WriteLine("  " + w);
        return 0;
    }

    private static int CmdSetDefault(string home, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("用法: codex-sync set-default <provider>");
            return 2;
        }
        var name = args[0];
        if (!ConfigService.IsProviderDefined(home, name))
        {
            Console.Error.WriteLine($"config.toml 里没有 [model_providers.{name}] 定义");
            return 2;
        }
        ConfigService.WriteProvider(home, name);
        Console.WriteLine($"已把 config.toml 顶层 model_provider 改为: {name}");
        return 0;
    }

    private static int CmdRestore(string home, Dictionary<string, string> opts, string[] sub)
    {
        if (opts.ContainsKey("list"))
        {
            var list = LegacyBackupService.ListAll(home);
            Console.WriteLine($"备份 ({list.Count}):");
            foreach (var b in list)
            {
                var t = b.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
                var fmt = b.Format == BackupFormat.Compact ? "紧凑" : "完整";
                var target = string.IsNullOrEmpty(b.TargetProvider) ? "" : "  → " + b.TargetProvider;
                Console.WriteLine($"  {b.Name}  {t}  [{fmt}]{target}  改 {b.ChangedFileCount}");
            }
            return 0;
        }

        var apply = opts.GetValueOrDefault("apply");
        if (string.IsNullOrEmpty(apply))
        {
            Console.Error.WriteLine("用法: codex-sync restore --list  或  codex-sync restore --apply <name>");
            return 2;
        }

        var found = LegacyBackupService.ListAll(home).FirstOrDefault(b => b.Name == apply);
        if (found == null)
        {
            Console.Error.WriteLine($"找不到备份: {apply}");
            return 2;
        }

        var progress = new Progress<string>(s => Console.WriteLine("  " + s));
        if (found.Format == BackupFormat.Compact)
        {
            var result = LegacyBackupService.RestoreCompact(home, found.Path, progress);
            Console.WriteLine($"\n完成: 还原 {result.FilesRestored} 个对话 / 数据库 {result.SqliteRowsUpdated}");
        }
        else
        {
            BackupService.RestoreBackup(home, found.Path);
            Console.WriteLine("完整备份还原完成");
        }
        return 0;
    }

    private static int CmdSmartRestore(string home)
    {
        Console.WriteLine("叠加还原所有紧凑格式备份...");
        var progress = new Progress<string>(s => Console.WriteLine("  " + s));
        var result = LegacyBackupService.RestoreOriginalDistribution(home, progress);
        Console.WriteLine($"\n完成: 还原 {result.FilesRestored} 个对话 / 数据库 {result.SqliteRowsUpdated}");
        Console.WriteLine("当前分布:");
        foreach (var kv in result.ResultDistribution.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Key,-15} {kv.Value}");
        return 0;
    }

    private static int CmdVersion()
    {
        Console.WriteLine($"CodexSyncWizard {UpdateCheckService.GetCurrentVersion()}");
        return 0;
    }
}
