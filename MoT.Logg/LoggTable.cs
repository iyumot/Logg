using Dapper;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MoT;

public class LoggTable<T>
{
    private readonly string _tableName;
    private readonly string _connStr;
    private readonly string _dbPath;
    private readonly SqlExpressionVisitor _visitor = new();

    private string? _orderBy;
    private int? _limit;
    private int? _offset; // 🚨 新增：支持 Skip
    private bool _isDistinct = false;
    private List<string>? _selectedColumns = null;
    private static readonly string[] _entityProperties;

    static LoggTable()
    {
        _entityProperties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
          .Where(p => p.CanRead).Select(p => p.Name).ToArray();

        if (typeof(T).GetConstructor(Type.EmptyTypes) == null)
        {
            SqlMapper.SetTypeMap(typeof(T), new FlexibleRecordTypeMap<T>());
        }

        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            var propType = prop.PropertyType;
            var underlyingType = Nullable.GetUnderlyingType(propType) ?? propType;

            bool isComplex = !underlyingType.IsPrimitive &&
                             !underlyingType.IsEnum &&
                             underlyingType != typeof(string) &&
                             underlyingType != typeof(DateTime) &&
                             underlyingType != typeof(DateTimeOffset) &&
                             underlyingType != typeof(Guid) &&
                             underlyingType != typeof(decimal) &&
                             underlyingType != typeof(byte[]) &&
                             underlyingType != typeof(TimeSpan);

            if (isComplex)
            {
                // 动态创建 JsonTypeHandler<propType> 并注册到 Dapper
                var handlerType = typeof(JsonTypeHandler<>).MakeGenericType(propType);
                var handler = Activator.CreateInstance(handlerType)!;

                var addMethod = typeof(SqlMapper).GetMethod("AddTypeHandler", new[] { typeof(Type), typeof(SqlMapper.ITypeHandler) });
                addMethod!.Invoke(null, new object[] { propType, handler });
            }
        }
    }

    internal LoggTable(string tableName, string connStr, string dbPath)
    {
        _tableName = tableName;
        _connStr = connStr;
        _dbPath = dbPath;
    }

    public LoggTable<T> Where(Expression<Func<T, bool>> predicate)
    {
        _visitor.Visit(predicate);
        return this;
    }

    public LoggTable<T> OrderBy(Expression<Func<T, object>> predicate)
    {
        _orderBy = $"[{GetMemberName(predicate)}] ASC";
        return this;
    }

    public LoggTable<T> OrderByDescending(Expression<Func<T, object>> predicate)
    {
        _orderBy = $"[{GetMemberName(predicate)}] DESC";
        return this;
    }

    // 🚨 新增：分页支持
    public LoggTable<T> Skip(int count)
    {
        _offset = count;
        return this;
    }

    public LoggTable<T> Take(int count)
    {
        _limit = count;
        return this;
    }

    // ================= 终端操作 (查询所有列 *) =================

    public T? FirstOrDefault()
    {
        _limit = 1;
        return ExecuteQuery<T>().FirstOrDefault();
    }

    public T? LastOrDefault()
    {
        if (string.IsNullOrEmpty(_orderBy)) _orderBy = "[_t_] DESC";
        _limit = 1;
        return ExecuteQuery<T>().FirstOrDefault();
    }

    public List<T> ToList() => ExecuteQuery<T>().ToList();


    public T[] ToArray() => ExecuteQuery<T>().ToArray();

    // ================= Select 投影 =================

    public LoggProjection<T, TResult> Select<TResult>(Expression<Func<T, TResult>> selector)
    {
        var columns = ExtractColumns(selector.Body);
        return new LoggProjection<T, TResult>(this, columns);
    }

    // ================= 内部 SQL 构建与执行 =================

    internal void SetDistinct(bool isDistinct) => _isDistinct = isDistinct;

    internal IEnumerable<TResult> ExecuteProjectionQuery<TResult>(List<string> columns)
    {
        _selectedColumns = columns;
        return ExecuteQuery<TResult>();
    }

    private string BuildSql()
    {
        var sb = new StringBuilder("SELECT ");

        if (_isDistinct) sb.Append("DISTINCT ");

        if (_selectedColumns != null && _selectedColumns.Count > 0)
        {
            // 如果使用了 Select 投影，只查指定的列
            sb.Append(string.Join(", ", _selectedColumns.Select(c => $"[{c}]")));
        }
        else
        {
            // 🚨 核心修复 2：抛弃 SELECT * ！
            // 只 SELECT 实体类 T 中真实定义的属性列。
            // 这样完美避开了生成器自动添加的 _t_ 字段，让 Dapper 能够精准匹配 record 的构造函数参数！
            sb.Append(string.Join(", ", _entityProperties.Select(p => $"[{p}]")));
        }

        sb.Append($" FROM [{_tableName}]");

        if (_visitor.HasWhere)
            sb.Append(" WHERE ").Append(_visitor.Sql);

        if (!string.IsNullOrEmpty(_orderBy))
            sb.Append(" ORDER BY ").Append(_orderBy);

        if (_limit.HasValue || _offset.HasValue)
            sb.Append($" LIMIT {(_limit.HasValue ? _limit.Value : -1)}");

        if (_offset.HasValue)
            sb.Append($" OFFSET {_offset.Value}");

        return sb.ToString();
    }

    private IEnumerable<TResult> ExecuteQuery<TResult>()
    {
        if (!File.Exists(_dbPath))
            return Enumerable.Empty<TResult>();

        var sql = BuildSql();
        using var conn = new SqliteConnection(_connStr);
        return conn.Query<TResult>(sql, _visitor.Parameters);
    }

    // ================= 表达式解析辅助方法 =================

    private static List<string> ExtractColumns(Expression expr)
    {
        var columns = new List<string>();

        if (expr is NewExpression newExpr)
        {
            foreach (var arg in newExpr.Arguments)
            {
                if (arg is MemberExpression m) columns.Add(m.Member.Name);
                else if (arg is UnaryExpression u && u.Operand is MemberExpression m2) columns.Add(m2.Member.Name);
            }
        }
        else if (expr is MemberInitExpression initExpr)
        {
            foreach (var binding in initExpr.Bindings)
            {
                if (binding is MemberAssignment assignment && assignment.Expression is MemberExpression m)
                    columns.Add(m.Member.Name);
            }
        }
        else if (expr is MemberExpression mExpr)
        {
            columns.Add(mExpr.Member.Name);
        }
        else if (expr is UnaryExpression unary && unary.Operand is MemberExpression um)
        {
            columns.Add(um.Member.Name);
        }

        return columns;
    }

    private static string GetMemberName(Expression<Func<T, object>> expr)
    {
        if (expr.Body is MemberExpression m) return m.Member.Name;
        if (expr.Body is UnaryExpression u && u.Operand is MemberExpression m2) return m2.Member.Name;
        throw new ArgumentException("Invalid expression");
    }
}

internal class FlexibleRecordTypeMap<T> : SqlMapper.ITypeMap
{
    private readonly ConstructorInfo _ctor;
    private readonly ParameterInfo[] _params;

    public FlexibleRecordTypeMap()
    {
        // 找到参数最多的构造函数 (通常是 record 的主构造函数)
        _ctor = typeof(T).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault() ?? throw new InvalidOperationException($"Type {typeof(T)} has no suitable constructor.");
        _params = _ctor.GetParameters();
    }

    public ConstructorInfo FindConstructor(string[] names, Type[] types)
    {
        // 核心魔法：只要列名匹配，就返回这个构造函数，忽略 types 的严格匹配
        if (names.Length == _params.Length)
        {
            bool match = true;
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.Equals(names[i], _params[i].Name, StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match) return _ctor;
        }
        return null!;
    }

    public ConstructorInfo FindExplicitConstructor() => null!;

    public SqlMapper.IMemberMap GetConstructorParameter(ConstructorInfo constructor, string columnName)
    {
        var param = _params.FirstOrDefault(p => string.Equals(p.Name, columnName, StringComparison.OrdinalIgnoreCase));
        if (param == null) return null!;
        return new SimpleMemberMap(columnName, param);
    }

    public SqlMapper.IMemberMap GetMember(string columnName) => null!;

    private class SimpleMemberMap : SqlMapper.IMemberMap
    {
        private readonly string _columnName;
        private readonly ParameterInfo _parameter;
        public SimpleMemberMap(string columnName, ParameterInfo parameter) { _columnName = columnName; _parameter = parameter; }
        public string ColumnName => _columnName;

        // 这里返回 int (ParameterType)，Dapper 底层会自动生成 IL 指令将 long 转换为 int
        public Type MemberType => _parameter.ParameterType;
        public PropertyInfo Property => null!;
        public FieldInfo Field => null!;
        public ParameterInfo Parameter => _parameter;
    }
}

internal class JsonTypeHandler<T> : SqlMapper.TypeHandler<T>
{
    public override void SetValue(IDbDataParameter parameter, T? value)
    {
        parameter.Value = value == null ? DBNull.Value : JsonSerializer.Serialize(value);
    }

    public override T? Parse(object value)
    {
        if (value is string str && !string.IsNullOrEmpty(str))
        {
            return JsonSerializer.Deserialize<T>(str);
        }
        return default;
    }
}






/// Select 投影后的包装器，支持在 SQL 层面执行 Distinct 和终端查询
/// </summary>
public class LoggProjection<T, TResult>
{
    private readonly LoggTable<T> _table;
    private readonly List<string> _columns;

    internal LoggProjection(LoggTable<T> table, List<string> columns)
    {
        _table = table;
        _columns = columns;
    }

    public LoggProjection<T, TResult> Distinct()
    {
        _table.SetDistinct(true);
        return this;
    }

    // 🚨 新增：投影后支持 Skip 和 Take
    public LoggProjection<T, TResult> Skip(int count)
    {
        _table.Skip(count); // 调用底层 table 的方法修改状态
        return this;
    }

    public LoggProjection<T, TResult> Take(int count)
    {
        _table.Take(count);
        return this;
    }

    public List<TResult> ToList()
    {
        return _table.ExecuteProjectionQuery<TResult>(_columns).ToList();
    }

    public TResult[] ToArray()
    {
        return ToList().ToArray();
    }

    public TResult? FirstOrDefault()
    {
        return _table.ExecuteProjectionQuery<TResult>(_columns).FirstOrDefault();
    }
}