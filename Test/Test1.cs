using Microsoft.Data.Sqlite;
using MoT; // 确保引用了您的核心库命名空间

namespace Test;

[TestClass]
public sealed class Test1
{
    private static readonly string DbPath = Path.Combine(AppContext.BaseDirectory, "app_logs.db");

    [TestInitialize]
    public void Setup()
    {
        // 1. 强制清空连接池，防止底层长连接锁住表导致 Drop 失败
        SqliteConnection.ClearAllPools();

        // 2. 重置 Logg 内部的内存状态 (清理“已建表”缓存，但保留“建表SQL”缓存)
        // 这样框架会认为表还没建，配合下面的物理 Drop，完美触发自动建表机制
        //Logg.ResetTestState();

        // 3. 连接数据库，删除所有用户表 (不删物理文件)
        using (var conn = new SqliteConnection($"Data Source={DbPath};Pooling=True;"))
        {
            conn.Open();

            // 查询所有非系统的用户表
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";

            var tables = new List<string>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            // 遍历并 Drop 掉所有表
            foreach (var table in tables)
            {
                using var dropCmd = conn.CreateCommand();
                // 使用 [] 包裹表名，防止表名是 SQL 关键字导致报错
                dropCmd.CommandText = $"DROP TABLE IF EXISTS [{table}];";
                dropCmd.ExecuteNonQuery();
            }

            // 4. (可选) 清理 WAL 文件，将主文件恢复到最干净的 0 字节状态
            using var checkpointCmd = conn.CreateCommand();
            checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpointCmd.ExecuteNonQuery();
        }
    }


    [TestMethod]
    public async Task TestAllTypesReadWrite()
    {
        // ==========================================
        // 1. 测试简单的 record (TestRecord)
        // ==========================================
        var expectedAlice = new TestRecord(1, "Alice");
        var expectedBob = new TestRecord(2, "Bob"); // 🚨 补回 Bob！

        Logg.Write(expectedAlice);
        Logg.Write(expectedBob);
        await Logg.FlushAsync();

        var actualAlice = Logg.Read<TestRecord>().Where(x => x.Id == 1).FirstOrDefault();
        var actualBob = Logg.Read<TestRecord>().Where(x => x.Id == 2).FirstOrDefault();

        Assert.IsNotNull(actualAlice, "Alice 未查询到");
        Assert.IsNotNull(actualBob, "Bob 未查询到");

        // 🚨 核心验证：record 默认支持值相等，直接对比
        Assert.AreEqual(expectedAlice, actualAlice, "TestRecord Alice 读取的值与写入的值不一致！");
        Assert.AreEqual(expectedBob, actualBob, "TestRecord Bob 读取的值与写入的值不一致！");


        // ==========================================
        // 2. 测试包含复杂类型 (struct) 的 record (TestRecord2)
        // ==========================================
        var expectedStruct = new TestStruct { Id = 100, Name = "StructName" };
        var expectedRecord2 = new TestRecord2(3, "Charlie", expectedStruct);

        Logg.Write(expectedRecord2);
        await Logg.FlushAsync();

        var actualRecord2 = Logg.Read<TestRecord2>()
            .Where(x => x.Id == 3)
            .FirstOrDefault();

        Assert.IsNotNull(actualRecord2, "TestRecord2 未查询到");

        // 🚨 核心验证 1：对比整个 record (包含内部的 struct)
        Assert.AreEqual(expectedRecord2, actualRecord2, "TestRecord2 整体值不一致！");

        // 🚨 核心验证 2：单独对比 struct
        Assert.AreEqual(expectedStruct, actualRecord2.Struct, "TestStruct 内部值反序列化后不一致！");


        // ==========================================
        // 3. 测试简单的 class (TestA)
        // ==========================================
        var expectedA = new TestA { Id = 4, Name = "David" };
        Logg.Write(expectedA);
        await Logg.FlushAsync();

        var actualA = Logg.Read<TestA>()
            .Where(x => x.Id == 4)
            .FirstOrDefault();

        Assert.IsNotNull(actualA, "TestA 未查询到");
        // 🚨 核心验证：普通 class 必须逐个属性断言
        Assert.AreEqual(expectedA.Id, actualA.Id, "TestA.Id 不一致");
        Assert.AreEqual(expectedA.Name, actualA.Name, "TestA.Name 不一致");


        // ==========================================
        // 4. 测试 Select 投影 与 Distinct 去重
        // ==========================================
        // 此时数据库里有：Alice, Bob, Charlie(在另一张表), David(在另一张表)
        // 我们再写入一个重复的 Alice (Id=5)
        Logg.Write(new TestRecord(5, "Alice"));
        await Logg.FlushAsync();

        // 现在 TestRecord 表里有：(1,Alice), (2,Bob), (5,Alice)
        // 生成 SQL: SELECT DISTINCT [Name] FROM [TestRecord]
        var distinctNames = Logg.Read<TestRecord>()
            .Select(x => x.Name)
            .Distinct()
            .ToList();

        // 🚨 逻辑闭环：去重后应该是 "Alice" 和 "Bob"，共 2 个元素
        Assert.HasCount(2, distinctNames, "Distinct 去重后的数量不对，预期为 2");

        CollectionAssert.Contains(distinctNames, "Alice");
        CollectionAssert.Contains(distinctNames, "Bob");
    }


    [TestMethod]
    public async Task TestAPIDebugCache()
    {
        // ==========================================
        // 1. 准备各种复杂场景的 Params 数据
        // ==========================================

        // 场景 A：Params 是匿名对象 (最常见)
        var paramsAnon = new { UserId = 123, Action = "Login", IsAdmin = true };
        var expected1 = new APIDebugCache("REQ-001", "POST", paramsAnon, "{\"status\":\"ok\"}");

        // 场景 B：Params 是 Dictionary
        var paramsDict = new Dictionary<string, int> { { "page", 1 }, { "size", 20 } };
        var expected2 = new APIDebugCache("REQ-002", "GET", paramsDict, null); // 测试 Json 为 null 的情况

        // 场景 C：Params 是简单字符串
        var expected3 = new APIDebugCache("REQ-003", "DELETE", "raw_string_param", "{}");

        // ==========================================
        // 2. 写入数据库
        // ==========================================
        Logg.Write(expected1);
        Logg.Write(expected2);
        Logg.Write(expected3);
        await Logg.FlushAsync();

        // ==========================================
        // 3. 读取并验证
        // ==========================================
        var actual1 = Logg.Read<APIDebugCache>().Where(x => x.Identifier == "REQ-001").FirstOrDefault();
        var actual2 = Logg.Read<APIDebugCache>().Where(x => x.Identifier == "REQ-002").FirstOrDefault();
        var actual3 = Logg.Read<APIDebugCache>().Where(x => x.Identifier == "REQ-003").FirstOrDefault();

        Assert.IsNotNull(actual1, "REQ-001 未查询到");
        Assert.IsNotNull(actual2, "REQ-002 未查询到");
        Assert.IsNotNull(actual3, "REQ-003 未查询到");

        // --- 验证基础字段 ---
        Assert.AreEqual(expected1.Method, actual1.Method);
        Assert.AreEqual(expected1.Json, actual1.Json);
        Assert.IsNull(actual2.Json, "REQ-002 的 Json 应该为 null");

        // --- 🚨 核心验证：object 类型的反序列化 (JsonElement) ---

        // 验证场景 A (匿名对象)
        var read = Logg.Read<APIDebugCache>().Where(x => x.Identifier == expected1.Identifier && x.Params == expected1.Params).FirstOrDefault();

        // 4. 验证结果
        Assert.IsNotNull(read, "使用匿名对象作为条件查询失败，未找到匹配记录！");
        Assert.AreEqual(expected1.Identifier, read.Identifier);
        Assert.AreEqual(expected1.Method, read.Method);

        // 验证读出来的 Params (由于是 object，会被还原为 JsonElement 或原类型)
        Assert.IsNotNull(read.Params);

         
    }

}

// ================= 实体定义 =================

public record TestRecord(int Id, string Name);

public record TestRecord2(int Id, string Name, TestStruct Struct);

public class TestA
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}

public struct TestStruct
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public record APIDebugCache(string Identifier, string Method, object Params, string Json);