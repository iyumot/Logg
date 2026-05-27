using Microsoft.Data.Sqlite;
using SQLitePCL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MoT;

public static class Logg
{
    private static readonly string DbPath = Path.Combine(AppContext.BaseDirectory, "app_logs.db");
    private static readonly string _connStr = $"Data Source={DbPath};Pooling=True;";

    // 只读连接字符串，防止查询时意外锁表或修改数据
    private static readonly string _readOnlyConnStr = $"Data Source={DbPath};Pooling=True;Mode=ReadOnly;";

    private static readonly Channel<LogJob> _channel = Channel.CreateBounded<LogJob>(
        new BoundedChannelOptions(100000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    private static readonly HashSet<string> _createdTables = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _tableSqlCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Type, string> _typeTableMap = new();

    private static bool _isWalSet = false;

    // 从 CREATE TABLE 语句中精准提取表名
    private static readonly Regex _tableNameRegex = new Regex(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:\[([^\]]+)\]|""([^""]+)""|'([^']+)'|(\w+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static Logg()
    {
        try { Batteries.Init(); } catch { }
        Task.Factory.StartNew(Consume, TaskCreationOptions.LongRunning);
    }

    private static string ExtractTableName(string createTableSql)
    {
        var match = _tableNameRegex.Match(createTableSql);
        if (!match.Success) throw new ArgumentException($"无法从 SQL 中解析出表名: {createTableSql}");
        return match.Groups[1].Success ? match.Groups[1].Value :
               match.Groups[2].Success ? match.Groups[2].Value :
               match.Groups[3].Success ? match.Groups[3].Value :
               match.Groups[4].Value;
    }

    private static void EnsureWal()
    {
        if (!_isWalSet)
        {
            lock (_createdTables)
            {
                if (!_isWalSet)
                {
                    try
                    {
                        using var c = new SqliteConnection(_connStr);
                        c.Open();
                        using var cmd = c.CreateCommand();
                        cmd.CommandText = "PRAGMA journal_mode=WAL;";
                        cmd.ExecuteNonQuery();
                        _isWalSet = true;
                    }
                    catch { }
                }
            }
        }
    }

    // 保持原有签名兼容生成器，但内部强制使用正则解析出的表名，确保绝对一致
    public static void Register<T>(Action<T> writer, string tableName, string createTableSql)
    {
        string parsedTableName = ExtractTableName(createTableSql); // 🚨 智能提取表名

        Router<T>.Writer = writer;
        lock (_tableSqlCache) { _tableSqlCache[parsedTableName] = createTableSql; }
        lock (_typeTableMap) { _typeTableMap[typeof(T)] = parsedTableName; }
        EnsureTableCreated(parsedTableName, createTableSql);
    }

    public static void EnsureTableCreated(string tableName, string createTableSql)
    {
        EnsureWal();
        bool added;
        lock (_createdTables) { added = _createdTables.Add(tableName); }

        if (added)
        {
            try
            {
                using var c = new SqliteConnection(_connStr);
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = createTableSql;
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    internal static class Router<T> { public static Action<T>? Writer; }

    public static void Write<T>(T v)
    {
        var w = Router<T>.Writer;
        w?.Invoke(v);
    }

    public static void Enqueue(LogJob job)
    {
        var spinWait = new SpinWait();
        while (!_channel.Writer.TryWrite(job)) spinWait.SpinOnce();
    }

    public static Task FlushAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new LogJob(tcs));
        return tcs.Task;
    }

    // 只读查询
    public static LoggTable<T> Read<T>()
    {
        string? tableName;
        lock (_typeTableMap) { _typeTableMap.TryGetValue(typeof(T), out tableName); }

        if (string.IsNullOrEmpty(tableName))
            throw new InvalidOperationException($"类型 {typeof(T).Name} 尚未注册，无法查询。");

        return new LoggTable<T>(tableName, _readOnlyConnStr, DbPath);
    }

    private static void Consume()
    {
        var batch = new List<LogJob>(1000);
        while (_channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_channel.Reader.TryRead(out var job))
            {
                if (job.FlushTcs != null)
                {
                    bool success = true;
                    if (batch.Count > 0) { success = WriteBatch(batch); batch.Clear(); }

                    if (success)
                    {
                        try
                        {
                            using var conn = new SqliteConnection(_connStr);
                            conn.Open();
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                            cmd.ExecuteNonQuery();
                        }
                        catch { }
                    }

                    if (success) job.FlushTcs.TrySetResult(true);
                    else job.FlushTcs.TrySetException(new Exception("Logg WriteBatch failed."));
                    continue;
                }
                batch.Add(job);
                if (batch.Count >= 1000) break;
            }
            if (batch.Count > 0) { WriteBatch(batch); batch.Clear(); }
        }
    }

    private static bool WriteBatch(List<LogJob> batch)
    {
        try
        {
            using var conn = new SqliteConnection(_connStr);
            conn.Open();
            using var tran = conn.BeginTransaction();
            SqliteCommand? cmd = null;
            string? currentSql = null;

            foreach (var job in batch)
            {
                if (job.InsertSql != currentSql)
                {
                    cmd?.Dispose();
                    cmd = conn.CreateCommand();
                    cmd.Transaction = tran;
                    cmd.CommandText = job.InsertSql!;
                    currentSql = job.InsertSql;
                }

                cmd!.Parameters.Clear();
                job.Binder!(cmd);

                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex) when (
                    ex.GetType().Name.Contains("SqliteException", StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
                {
                    var match = _tableNameRegex.Match(ex.Message.Contains("no such table:") ? ex.Message : $"no such table: {job.TableName}");
                    // 兜底：如果异常里取不到，从 job 里取
                    string missingTableName = job.TableName ?? ExtractTableName(job.InsertSql!.Replace("INSERT INTO", "CREATE TABLE"));

                    // 尝试从 INSERT 语句提取表名 (简易正则)
                    var insertMatch = Regex.Match(job.InsertSql!, @"INSERT\s+INTO\s+(?:\[([^\]]+)\]|""([^""]+)""|'([^']+)'|(\w+))", RegexOptions.IgnoreCase);
                    if (insertMatch.Success)
                    {
                        missingTableName = insertMatch.Groups[1].Success ? insertMatch.Groups[1].Value :
                                           insertMatch.Groups[2].Success ? insertMatch.Groups[2].Value :
                                           insertMatch.Groups[3].Success ? insertMatch.Groups[3].Value :
                                           insertMatch.Groups[4].Value;
                    }

                    string? createSql;
                    lock (_tableSqlCache) { _tableSqlCache.TryGetValue(missingTableName, out createSql); }

                    if (!string.IsNullOrEmpty(createSql))
                    {
                        using var createCmd = conn.CreateCommand();
                        createCmd.Transaction = tran;
                        createCmd.CommandText = createSql;
                        createCmd.ExecuteNonQuery();
                        lock (_createdTables) { _createdTables.Add(missingTableName); }

                        using var retryCmd = conn.CreateCommand();
                        retryCmd.Transaction = tran;
                        retryCmd.CommandText = job.InsertSql!;
                        job.Binder!(retryCmd);
                        retryCmd.ExecuteNonQuery();
                    }
                    else { throw; }
                }
            }
            tran.Commit();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Logg Error] 批量写入失败: {ex.Message}");
            return false;
        }
    }

    // --- 内置结构化日志部分 (保持不变) ---
    private static readonly string _defaultTableName = "_def_logs_";
    private static bool _isDefaultRegistered = false;
    private static readonly object _regLock = new object();


    private static void EnsureDefaultRegistered()
    {
        if (_isDefaultRegistered) return;
        lock (_regLock)
        {
            if (_isDefaultRegistered) return;
            var sql = $"CREATE TABLE IF NOT EXISTS {_defaultTableName} (Timestamp TEXT, Level INTEGER, Caller TEXT, Message TEXT, Exception TEXT, Properties TEXT)";
            Register<LogEvent>(WriteLogEvent, _defaultTableName, sql);
            _isDefaultRegistered = true;
        }
    }

    private static void WriteLogEvent(LogEvent evt)
    {
        var job = new LogJob(
            $"INSERT INTO {_defaultTableName} (Timestamp, Level, Caller, Message, Exception, Properties) VALUES ($t, $l, $c, $m, $e, $p)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", evt.Timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("$l", (int)evt.Level);
                cmd.Parameters.AddWithValue("$c", evt.Caller);
                cmd.Parameters.AddWithValue("$m", evt.Message);
                cmd.Parameters.AddWithValue("$e", (object?)evt.Exception ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$p", (object?)evt.Properties ?? DBNull.Value);
            },
            _defaultTableName
        );
        Enqueue(job);
    }


    private static void WriteLog(LogLevel level, Exception? ex, string? message, string callerMemberName, params object?[]? propertyValues)
    {
        EnsureDefaultRegistered();

        var evt = new LogEvent
        {
            Level = level,
            Message = message ?? "",
            Exception = ex?.ToString(),
            Properties = propertyValues?.Length is null or 0 ? null : JsonSerializer.Serialize(propertyValues),
            Caller = callerMemberName
        };

        Write(evt);
    }



    public static void Verbose(string? message = null, [CallerMemberName] string caller = "", params object?[]? propertyValues) =>
        WriteLog(LogLevel.Verbose, null, message , caller, propertyValues);

    public static void Debug(string? message = null, [CallerMemberName] string caller = "", params object?[]? propertyValues) =>
        WriteLog(LogLevel.Debug, null, message , caller, propertyValues);

    public static void Information(string? message = null, [CallerMemberName] string caller = "", params object?[]? propertyValues) =>
        WriteLog(LogLevel.Information, null, message , caller, propertyValues);

    public static void Warning(string? message = null, [CallerMemberName] string caller = "", params object?[]? propertyValues) =>
        WriteLog(LogLevel.Warning, null, message , caller, propertyValues);

    public static void Error(Exception? exception, string? message = null, [CallerMemberName] string caller = "", params object?[]? propertyValues) =>
        WriteLog(LogLevel.Error, exception, message , caller, propertyValues);

    public static void Error(string? message = null, [CallerMemberName] string caller = "", params object?[]? propertyValues) =>
        WriteLog(LogLevel.Error, null, message , caller, propertyValues);

    public static void Fatal(Exception? exception, string? message = null, [CallerMemberName] string caller = "", params object?[]? propertyValues) =>
        WriteLog(LogLevel.Fatal, exception, message , caller, propertyValues);

    public static void Fatal(string? message = null, [CallerMemberName] string caller = "", params object?[]? propertyValues) =>
        WriteLog(LogLevel.Fatal, null, message , caller, propertyValues);


}

public readonly struct LogJob
{
    public readonly string? InsertSql;
    public readonly Action<SqliteCommand>? Binder;
    public readonly TaskCompletionSource<bool>? FlushTcs;
    public readonly string? TableName;

    public LogJob(string i, Action<SqliteCommand> b, string? tableName = null) { InsertSql = i; Binder = b; FlushTcs = null; TableName = tableName; }
    public LogJob(TaskCompletionSource<bool> tcs) { InsertSql = null; Binder = null; FlushTcs = tcs; TableName = null; }
}

public enum LogLevel { Verbose = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Fatal = 5 }
public class LogEvent
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public LogLevel Level { get; set; }
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
    public string? Properties { get; set; }
    public string? Caller { get; set; }
}
