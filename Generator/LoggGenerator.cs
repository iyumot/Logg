using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

[Generator]
public class LoggGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var loggedTypes = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (s, _) => s is InvocationExpressionSyntax,
            transform: static (ctx, _) =>
            {
                if (ctx.Node is not InvocationExpressionSyntax invocation) return null;

                var symbolInfo = ctx.SemanticModel.GetSymbolInfo(invocation);
                if (symbolInfo.Symbol is not IMethodSymbol methodSymbol) return null;

                if (methodSymbol.Name != "Write" ||
                    methodSymbol.ContainingType.Name != "Logg" ||
                    methodSymbol.ContainingNamespace.ToDisplayString() != "MoT")
                {
                    return null;
                }

                ITypeSymbol? loggedType = null;

                if (methodSymbol.IsGenericMethod && methodSymbol.TypeArguments.Length > 0)
                    loggedType = methodSymbol.TypeArguments[0];
                else if (!methodSymbol.IsGenericMethod && methodSymbol.Parameters.Length > 0)
                    loggedType = methodSymbol.Parameters[0].Type;

                if (loggedType is null) return null;

                if (loggedType.ToDisplayString() == "MoT.LogEvent") return null;
                if (loggedType.SpecialType != SpecialType.None) return null;

                // 🚨 优化 1：在 transform 阶段直接计算好 SafeName (处理泛型)
                string safeName = loggedType.Name;
                if (loggedType is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
                {
                    safeName += "_" + namedType.TypeArguments[0].Name;
                }

                // 🚨 优化 2：在 transform 阶段提取属性，并计算是否需要 JSON 序列化
                var properties = loggedType.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer)
                    .Select(p =>
                    {
                        bool needsJson = true; // 默认所有复杂类型都需要序列化
                        var propType = p.Type;

                        // 1. 剥开 Nullable<T> 的外衣
                        if (propType is INamedTypeSymbol namedType &&
                            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                        {
                            propType = namedType.TypeArguments[0];
                        }

                        // 🚨 核心修复：使用白名单精确判断哪些类型【不需要】序列化
                        if (propType.SpecialType is
                            SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte or
                            SpecialType.System_Int16 or SpecialType.System_UInt16 or
                            SpecialType.System_Int32 or SpecialType.System_UInt32 or
                            SpecialType.System_Int64 or SpecialType.System_UInt64 or
                            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or
                            SpecialType.System_Char or SpecialType.System_String)
                        {
                            needsJson = false; // 基础数值、布尔、字符、字符串
                        }
                        else if (propType.TypeKind == TypeKind.Enum)
                        {
                            needsJson = false; // 枚举
                        }
                        else if (propType.Name is "DateTime" or "DateTimeOffset" or "Guid" or "TimeSpan")
                        {
                            needsJson = false; // SQLite 原生支持的特殊 struct
                        }
                        else if (propType is IArrayTypeSymbol arrayType && arrayType.ElementType.SpecialType == SpecialType.System_Byte)
                        {
                            needsJson = false; // byte[] (BLOB)
                        }

                        // 注意：如果 propType 是 object (SpecialType.System_Object)，
                        // 或者任何自定义 class/struct，它都会跳过上面的 if，保持 needsJson = true！

                        bool canBeNull = p.Type.IsReferenceType ||
                                         (p.Type.IsValueType && p.Type is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

                        return new PropertyInfo(
                            p.Name,
                            MapToSqliteType(p.Type),
                            needsJson,
                            canBeNull
                        );
                    })
                    .ToList();

                return new LogTypeInfo(
                    FullyQualifiedName: loggedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    ClassName: loggedType.Name,
                    SafeName: safeName,
                    Namespace: loggedType.ContainingNamespace.ToDisplayString(),
                    Properties: properties
                );
            })
            .Where(static t => t is not null)
            .Collect()
            .Select(static (list, _) =>
            {
                return list.Where(t => t is not null)
                           .Select(t => t!)
                           .AsEnumerable()
                           .GroupBy(t => t.FullyQualifiedName)
                           .Select(g => g.First())
                           .ToImmutableArray();
            });

        // 🚨 核心修复：types 的类型现在是 ImmutableArray<LogTypeInfo>，绝对不能再 Cast 了！
        context.RegisterSourceOutput(loggedTypes, static (spc, types) =>
        {
            if (types.IsDefaultOrEmpty) return;

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("using System;");
            sb.AppendLine("using Microsoft.Data.Sqlite;");
            sb.AppendLine("using System.Text.Json;");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine();
            sb.AppendLine("namespace MoT");
            sb.AppendLine("{");

            sb.AppendLine("    internal static class LoggRegistrar");
            sb.AppendLine("    {");
            sb.AppendLine("        [ModuleInitializer]");
            sb.AppendLine("        internal static void Initialize()");
            sb.AppendLine("        {");

            // ✅ 直接使用 LogTypeInfo 的属性
            foreach (var type in types)
            {
                sb.AppendLine($"            Logg.Register<{type.FullyQualifiedName}>({type.SafeName}_Writer.Write, \"{type.SafeName}\", {type.SafeName}_Writer.CreateTableSql);");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            foreach (var type in types)
            {
                GenerateSpecificWriter(sb, type);
            }

            sb.AppendLine("}");
            spc.AddSource("Logg.Generated.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        });
    }

    // 🚨 核心修复：参数改为 LogTypeInfo，彻底解耦 Roslyn Symbol
    private static void GenerateSpecificWriter(StringBuilder sb, LogTypeInfo type)
    {
        sb.AppendLine($"    internal static class {type.SafeName}_Writer");
        sb.AppendLine("    {");

        // --- CreateTable SQL ---
        sb.AppendLine("        internal const string CreateTableSql = @\"");
        sb.AppendLine($"            CREATE TABLE IF NOT EXISTS [{type.SafeName}] (");
        sb.AppendLine("                [_t_] TEXT NOT NULL,");
        for (int i = 0; i < type.Properties.Count; i++)
        {
            string comma = i == type.Properties.Count - 1 ? "" : ",";
            var prop = type.Properties[i];
            sb.AppendLine($"                [{prop.Name}] {prop.SqliteType}{comma}");
        }
        sb.AppendLine("            );\";");
        sb.AppendLine();

        // --- Insert SQL ---
        var cols = string.Join(", ", type.Properties.Select(p => $"[{p.Name}]"));
        var pars = string.Join(", ", type.Properties.Select(p => $"@{p.Name}"));
        sb.AppendLine("        private const string InsertSql = @\"");
        sb.AppendLine($"            INSERT INTO [{type.SafeName}] ([_t_], {cols})");
        sb.AppendLine($"            VALUES (@_t_, {pars});\";");
        sb.AppendLine();

        // --- Write 方法 ---
        sb.AppendLine($"        public static void Write({type.FullyQualifiedName} v)");
        sb.AppendLine("        {");
        sb.AppendLine($"            Logg.Enqueue(new LogJob(InsertSql, cmd => BindParameters(cmd, v), \"{type.SafeName}\"));"); // 传入表名激活自动建表
        sb.AppendLine("        }");
        sb.AppendLine();

        // --- BindParameters 方法 ---
        sb.AppendLine($"        private static void BindParameters(SqliteCommand cmd, {type.FullyQualifiedName} v)");
        sb.AppendLine("        {");
        sb.AppendLine("            cmd.Parameters.Clear();");
        sb.AppendLine("            cmd.Parameters.AddWithValue(\"@_t_\", DateTime.Now.ToString(\"o\"));");

        foreach (var p in type.Properties)
        {
            string valExpr;

            if (p.CanBeNull)
            {
                // ✅ 可能为 null 的类型 (如 string, DateTime?, int?, 自定义 class)
                // 保留 == null 判断，转为 DBNull.Value
                valExpr = p.NeedsJsonSerialization
                    ? $"v.{p.Name} == null ? DBNull.Value : JsonSerializer.Serialize(v.{p.Name})"
                    : $"v.{p.Name} == null ? DBNull.Value : v.{p.Name}";
            }
            else
            {
                // ✅ 绝对不可能为 null 的类型 (如 DateTime, int, bool, Guid)
                // 🚨 核心修复：直接传值，坚决不生成 "== null" 判断，彻底消除 CS8073 警告！
                valExpr = p.NeedsJsonSerialization
                    ? $"JsonSerializer.Serialize(v.{p.Name})"
                    : $"v.{p.Name}";
            }

            sb.AppendLine($"            cmd.Parameters.AddWithValue(\"@{p.Name}\", {valExpr});");
        }
        sb.AppendLine("        }");

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static string MapToSqliteType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol namedType &&
            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            type = namedType.TypeArguments[0];
        }

        return type.SpecialType switch
        {
            SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 or
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Boolean => "INTEGER",

            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal => "REAL",

            _ => "TEXT"
        };
    }

    // 🚨 优化：增加 SafeName 和 NeedsJsonSerialization 字段
    public record LogTypeInfo(
        string FullyQualifiedName,
        string ClassName,
        string SafeName,           // 用于生成类名、表名，已处理泛型
        string Namespace,
        List<PropertyInfo> Properties
    );

    public record PropertyInfo(
        string Name,
        string SqliteType,
        bool NeedsJsonSerialization,
        bool CanBeNull
    );
}