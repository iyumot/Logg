using Dapper;
using System;
using System.Linq.Expressions;
using System.Text.Json;

namespace MoT;

internal class SqlExpressionVisitor : ExpressionVisitor
{
    public string Sql { get; private set; } = "";
    public DynamicParameters Parameters { get; } = new();
    public bool HasWhere => !string.IsNullOrEmpty(Sql);

    private int _paramIndex = 0;

    protected override Expression VisitBinary(BinaryExpression node)
    {
        if (node.NodeType == ExpressionType.AndAlso)
        {
            Sql += "("; Visit(node.Left); Sql += " AND "; Visit(node.Right); Sql += ")";
        }
        else if (node.NodeType == ExpressionType.OrElse)
        {
            Sql += "("; Visit(node.Left); Sql += " OR "; Visit(node.Right); Sql += ")";
        }
        else
        {
            if (node.Left is not MemberExpression member)
                throw new NotSupportedException("仅支持 'x.Property == value' 格式的二元操作");

            string colName = member.Member.Name;
            object? value = ExtractValue(node.Right);

            string op = node.NodeType switch
            {
                ExpressionType.Equal => "=",
                ExpressionType.NotEqual => "!=",
                ExpressionType.GreaterThan => ">",
                ExpressionType.LessThan => "<",
                ExpressionType.GreaterThanOrEqual => ">=",
                ExpressionType.LessThanOrEqual => "<=",
                _ => throw new NotSupportedException($"不支持的操作符: {node.NodeType}")
            };

            string pName = $"p{_paramIndex++}";
            Sql += $"[{colName}] {op} @{pName}";

            // 🚨 核心修复：对参数值进行标准化（复杂类型自动转 JSON）
            Parameters.Add(pName, NormalizeValue(value));
        }
        return node;
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Method.DeclaringType == typeof(string))
        {
            if (node.Method.Name is "Contains" or "StartsWith" or "EndsWith")
            {
                if (node.Object is not MemberExpression member)
                    throw new NotSupportedException("仅支持 'x.Property.Contains(value)' 格式");

                string colName = member.Member.Name;
                object? rawValue = ExtractValue(node.Arguments[0]);
                string strValue = rawValue?.ToString() ?? "";

                string likeValue = node.Method.Name switch
                {
                    "Contains" => $"%{strValue}%",
                    "StartsWith" => $"{strValue}%",
                    "EndsWith" => $"%{strValue}",
                    _ => strValue
                };

                string pName = $"p{_paramIndex++}";
                Sql += $"[{colName}] LIKE @{pName}";
                Parameters.Add(pName, likeValue);
                return node;
            }
        }

        if (node.Method.Name == "Contains")
        {
            MemberExpression? memberExpr = null;
            Expression? collectionExpr = null;

            if (node.Object != null && node.Arguments.Count == 1 && node.Arguments[0] is MemberExpression m1)
            { memberExpr = m1; collectionExpr = node.Object; }
            else if (node.Object == null && node.Arguments.Count == 2 && node.Arguments[1] is MemberExpression m2)
            { memberExpr = m2; collectionExpr = node.Arguments[0]; }

            if (memberExpr != null && collectionExpr != null)
            {
                string colName = memberExpr.Member.Name;
                object? listValue = ExtractValue(collectionExpr);

                string pName = $"p{_paramIndex++}";
                Sql += $"[{colName}] IN @{pName}";

                // 🚨 核心修复：如果是复杂类型的集合，也需要序列化
                Parameters.Add(pName, NormalizeValue(listValue));
                return node;
            }
        }

        return base.VisitMethodCall(node);
    }

    private object? ExtractValue(Expression expr)
    {
        if (expr is ConstantExpression c) return c.Value;
        var lambda = Expression.Lambda(expr);
        return lambda.Compile().DynamicInvoke();
    }

    // 🚨 核心魔法：判断类型，如果是复杂类型则序列化为 JSON，保持与生成器写入逻辑一致
    private object? NormalizeValue(object? value)
    {
        if (value == null) return DBNull.Value;

        var type = value.GetType();

        // SQLite/Dapper 原生支持的基础类型，直接放行
        if (type.IsPrimitive || type.IsEnum || value is string || value is DateTime ||
            value is DateTimeOffset || value is Guid || value is byte[] ||
            value is decimal || value is TimeSpan)
        {
            return value;
        }

        // Nullable 基础类型
        var underlyingType = Nullable.GetUnderlyingType(type);
        if (underlyingType != null && (underlyingType.IsPrimitive || underlyingType.IsEnum ||
            underlyingType == typeof(DateTime) || underlyingType == typeof(Guid) ||
            underlyingType == typeof(decimal)))
        {
            return value;
        }

        // 其他所有复杂类型（如 KeyValuePair, List, Dictionary, 自定义 Class），序列化为 JSON 字符串
        return JsonSerializer.Serialize(value);
    }
}