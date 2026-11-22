// scan-waylithclr：列一个目录里所有程序集对 WaylithClr.* 的类型/成员引用，以及目录里
// 定义的 WaylithClr.* 类型（PortHooks.dll 替身自查）。用途：
//   1. 游戏更新后核对引用面 vs 替身面（安卓 TECHNICAL.md 3.4"比较两份 CoreLib 公开成员"
//      的 WaylithClr 对应物——缺了哪个成员，引擎一碰到就 MissingMethodException）；
//   2. setup 改道前后各跑一遍：引用的 scope 应从 System.Runtime 变成 PortHooks。
// 带第二个参数（替身所在目录）时进入校验模式：把每个 scope=PortHooks 的 WaylithClr
// 成员引用真实解析一遍——这正是运行时装载要做的事，缺类型/缺成员当场点名。
// 用法：dotnet run --project scripts/scan-waylithclr -- <目录> [PortHooks所在目录]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: scan-waylithclr <目录> [PortHooks所在目录]");
            return 2;
        }
        string dir = args[0];
        if (args.Length >= 2)
            return ResolveCheck(dir, args[1]);
        // (引用它的 dll, 类型全名, scope 名) -> 成员名集合
        var refs = new SortedDictionary<string, Dictionary<string, Dictionary<string, SortedSet<string>>>>();
        var defs = new SortedDictionary<string, List<string>>();
        foreach (string path in Directory.EnumerateFiles(dir, "*.dll"))
        {
            string name = Path.GetFileName(path);
            // 游戏自带的定制运行时件定义着真身（还有转发）——单列，不参与引用统计
            try
            {
                using var asm = AssemblyDefinition.ReadAssembly(path,
                    new ReaderParameters { ReadingMode = ReadingMode.Immediate });
                foreach (TypeDefinition t in asm.MainModule.Types)
                    CollectDefs(t, name, defs);
                foreach (TypeReference t in asm.MainModule.GetTypeReferences())
                {
                    if (!t.FullName.StartsWith("WaylithClr.", StringComparison.Ordinal))
                        continue;
                    string scope = t.Scope switch
                    {
                        AssemblyNameReference a => "asm:" + a.Name,
                        ModuleDefinition => "mod:本模块",
                        _ => t.Scope?.ToString() ?? "?",
                    };
                    GetRefEntry(refs, name, t.FullName)[scope] = new SortedSet<string>();
                }
                foreach (MemberReference m in asm.MainModule.GetMemberReferences())
                {
                    TypeReference d = m.DeclaringType;
                    if (d == null || !d.FullName.StartsWith("WaylithClr.", StringComparison.Ordinal))
                        continue;
                    // 成员引用的类型链（嵌套类型挂在声明者上）都算到最外层 WaylithClr 名下
                    for (TypeReference t = d; t != null; t = t.DeclaringType)
                    {
                        if (!t.FullName.StartsWith("WaylithClr.", StringComparison.Ordinal))
                            continue;
                        string scope = t.Scope switch
                        {
                            AssemblyNameReference a => "asm:" + a.Name,
                            ModuleDefinition => "mod:本模块",
                            _ => t.Scope?.ToString() ?? "?",
                        };
                        var byScope = GetRefEntry(refs, name, t.FullName);
                        if (!byScope.TryGetValue(scope, out var members))
                            byScope[scope] = members = new SortedSet<string>();
                        members.Add(m is MethodReference ? m.FullName : m.Name);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{name}: 读不了（{e.GetType().Name}: {e.Message}）");
            }
        }
        Console.WriteLine("==== WaylithClr.* 引用（按 dll） ====");
        foreach (var (dll, types) in refs)
        {
            Console.WriteLine($"{dll}:");
            foreach (var (type, scopes) in types)
                foreach (var (scope, members) in scopes)
                    Console.WriteLine($"  {type}  [{scope}]  {string.Join(", ", members)}");
        }
        if (refs.Count == 0)
            Console.WriteLine("（无）");
        Console.WriteLine("==== WaylithClr.* 定义（含替身） ====");
        foreach (var (dll, types) in defs)
        {
            Console.WriteLine($"{dll}: {types.Count} 个类型");
            foreach (string t in types)
                Console.WriteLine("  " + t);
        }
        if (defs.Count == 0)
            Console.WriteLine("（无）");
        return 0;
    }

    // 校验模式：scope=PortHooks 的 WaylithClr 成员引用逐个 Resolve。全绿 = 改道后
    // 运行时按名字+签名能找到每个成员；任何 MissingMethod/MissingMethod 体面的报错
    // 都说明替身面要补（Android 端跟 4998，iOS 端跟 5014——每轮游戏更新都该重跑）。
    private static int ResolveCheck(string dir, string hooksDir)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(hooksDir);
        resolver.AddSearchDirectory(dir);
        int checked_ = 0, failed = 0;
        foreach (string path in Directory.EnumerateFiles(dir, "*.dll"))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("System", StringComparison.Ordinal))
                continue;
            try
            {
                using var asm = AssemblyDefinition.ReadAssembly(path,
                    new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
                foreach (MemberReference m in asm.MainModule.GetMemberReferences())
                {
                    TypeReference d = m.DeclaringType;
                    if (d == null || !d.FullName.StartsWith("WaylithClr.", StringComparison.Ordinal))
                        continue;
                    bool viaPortHooks = false;
                    for (TypeReference t = d; t != null; t = t.DeclaringType)
                        if (t.Scope is AssemblyNameReference a && a.Name == "PortHooks")
                            viaPortHooks = true;
                    if (!viaPortHooks)
                        continue;
                    checked_++;
                    try
                    {
                        _ = m.Resolve();
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Console.WriteLine($"❌ {name}: {m.FullName} —— {e.Message.Split('\n')[0]}");
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{name}: 读不了（{e.GetType().Name}: {e.Message}）");
            }
        }
        Console.WriteLine(checked_ > 0
            ? (failed == 0 ? $"✅ {checked_} 处 WaylithClr 成员引用全部解析到 PortHooks" : $"❌ {checked_} 处里 {failed} 处解析失败")
            : "⚠️ 没有 scope=PortHooks 的引用（还没改道？）");
        return failed == 0 ? 0 : 1;
    }

    private static void CollectDefs(TypeDefinition t, string dll, SortedDictionary<string, List<string>> defs)
    {
        if (t.Namespace == "WaylithClr")
        {
            if (!defs.TryGetValue(dll, out var list))
                defs[dll] = list = new List<string>();
            list.Add(t.FullName + (t.NestedTypes.Count > 0 ? $"（+{t.NestedTypes.Count} 嵌套）" : ""));
        }
        foreach (TypeDefinition n in t.NestedTypes)
            CollectDefs(n, dll, defs);
    }

    private static Dictionary<string, SortedSet<string>> GetRefEntry(
        SortedDictionary<string, Dictionary<string, Dictionary<string, SortedSet<string>>>> refs,
        string dll, string type)
    {
        if (!refs.TryGetValue(dll, out var types))
            refs[dll] = types = new Dictionary<string, Dictionary<string, SortedSet<string>>>();
        if (!types.TryGetValue(type, out var scopes))
            types[type] = scopes = new Dictionary<string, SortedSet<string>>();
        return scopes;
    }
}
