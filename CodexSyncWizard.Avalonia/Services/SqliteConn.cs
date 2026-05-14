using Microsoft.Data.Sqlite;

namespace CodexSyncWizard.Services;

/// <summary>
/// 统一构造 SqliteConnection。
///
/// 关键点: 强制 Pooling=False。
/// Microsoft.Data.Sqlite 默认开连接池 — 即使 using 释放 SqliteConnection，
/// 底层物理句柄会被 pool 缓存，导致 state_5.sqlite-wal / -shm 看似关了但实际还被占。
/// 这会让随后的"还原"操作删 -wal 失败（"文件被进程持有"），而那个进程其实就是我们自己。
/// 关掉池后每次新连接 → 释放即关；性能损耗对本工具忽略不计（操作粒度是秒级，不是毫秒级）。
/// </summary>
public static class SqliteConn
{
    public static SqliteConnection Open(string path, string? mode = null)
    {
        var cs = $"Data Source={path};Pooling=False";
        if (mode != null) cs += $";Mode={mode}";
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// 临时兜底: 在做"必须独占文件"的危险操作前调一次，把 ADO.NET 池里残留的连接也清掉。
    /// 配合 Pooling=False 是双保险（防止历史代码或第三方包打开过连接）。
    /// </summary>
    public static void ClearAllPools() => SqliteConnection.ClearAllPools();
}
