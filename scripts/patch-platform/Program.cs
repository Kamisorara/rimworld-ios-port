// 平台改写器（安卓 patch/Patch.cs 的 iOS 雏形）：在 PC 上对 game 副本跑，iOS 的每一面
// 硬墙在这里各成一个改写条目——安卓那套实测可行的路线，同一副骨架。
//
// 用法：dotnet run --project scripts/patch-platform -- <game目录>
// 幂等：改写先探测目标已是改后形态就跳过；写临时文件再替换（Write 中途崩不毁原文件）。
// 全部成功写 platform.port17 戳（pack-game-zip.py 打包前会自动跑本工具与 patch-os-detect.py）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Port17Patch
{
    internal static class Program
    {
        // ---- 条目一（方法 null 化）：Waylith.dll: ProcessLifetime.RegisterPosixSignal → return null。
        // iOS 的 .NET 构建里 PosixSignalRegistration.Create 是 PlatformNotSupportedException
        // （真机首跑撞的）；安卓把它替换成自家的 sigaction 注册，iOS 连信号交互都没有，
        // null 正是 Windows 一直走的路径（字段可空，Dispose 全部 ?.）。
        private static readonly (string dll, string type, string method)[] NullReturns =
        {
            ("Waylith.dll", "Waylith.Diagnostics.ProcessLifetime", "RegisterPosixSignal"),
        };

        // ---- 条目二（删事件订阅）：Console.CancelKeyPress 的 += / -= 整五条 IL 删掉。
        // System.Console 在 POSIX 上用 PosixSignalRegistration 实现 CancelKeyPress（监听
        // SIGINT）——同一个不支持的 API 的下一个使用者（真机三跑）。Hook() 订阅、Dispose()
        // 退订对称处理；其余三个钩子（ProcessExit/UnhandledException/Unloading）iOS 支持，
        // 保留。安卓改写表的第二、三条就是这两个调用。
        private static readonly (string dll, string type, string method)[] StripCancelKeyPress =
        {
            ("Waylith.dll", "Waylith.Diagnostics.ProcessLifetime", "Hook"),
            ("Waylith.dll", "Waylith.Diagnostics.ProcessLifetime", "Dispose"),
        };

        private static string gameDir;
        private static readonly List<string> applied = new();

        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("用法：dotnet run --project scripts/patch-platform -- <game目录>");
                return 2;
            }
            gameDir = args[0];
            int changed = 0, kept = 0;

            foreach (var entry in NullReturns)
            {
                var (hit, did) = WithMethod(entry, m =>
                {
                    if (IsNulled(m))
                        return (false, true);
                    m.Body = new MethodBody(m) { InitLocals = false, MaxStackSize = 1 };
                    var il = m.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ret);
                    return (true, true);
                });
                if (hit) Report(entry, "→ return null（iOS 无 PosixSignalRegistration；null 即 Windows 路径）", "=null", ref changed, ref kept);
                else if (did) ReportKept(entry, "已是 return null", ref kept);
            }

            foreach (var entry in StripCancelKeyPress)
            {
                var (hit, did) = WithMethod(entry, m =>
                {
                    int n = StripConsoleCancelKey(m);
                    return (n > 0, false);
                });
                if (hit) Report(entry, $"Console.CancelKeyPress 订阅已删", "=-CancelKeyPress", ref changed, ref kept);
                else ReportKept(entry, "无 Console.CancelKeyPress 订阅（不用动）", ref kept);
            }

            string stamp = Path.Combine(gameDir, "platform.port17");
            var stampLines = applied.ToList();
            if (File.Exists(stamp))
                stampLines = File.ReadAllLines(stamp).Union(applied).ToList();
            File.WriteAllLines(stamp, stampLines);
            Console.WriteLine($"改写 {changed} 处，幂等跳过 {kept} 处；戳 platform.port17 在");
            return 0;
        }

        private static void Report((string dll, string type, string method) e, string what, string tag,
            ref int changed, ref int kept)
        {
            Console.WriteLine($"{e.dll}: {e.type}::{e.method} {what}");
            applied.Add($"{e.dll}:{e.type}::{e.method}{tag}");
            changed++;
        }

        private static void ReportKept((string dll, string type, string method) e, string why, ref int kept)
        {
            Console.WriteLine($"{e.dll}: {e.type}::{e.method} {why}");
            kept++;
        }

        /// <summary>打开条目的 dll，找类型与方法，跑改写；改了就写临时文件再替换原文件。
        /// 返回 (改了没有, 方法体已检测为改后形态)。</summary>
        private static (bool changedIt, bool already) WithMethod(
            (string dll, string type, string method) entry,
            Func<MethodDefinition, (bool apply, bool already)> rewrite)
        {
            string path = Path.Combine(gameDir, entry.dll);
            if (!File.Exists(path))
            {
                Console.WriteLine($"{entry.dll}: 不在（这个目录里没有它）");
                return (false, false);
            }
            // 安卓 Patch.cs 的读取配方（实测可行）：Immediate 全解析 + 依赖目录（不用 InMemory——
            // Cecil 0.11.x 的 InMemory 路径在写出时会踩 PatchRawCode 的空引用）
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(gameDir);
            using var asm = AssemblyDefinition.ReadAssembly(path,
                new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
            var target = asm.MainModule.Types.FirstOrDefault(t => t.FullName == entry.type)
                ?? throw new InvalidOperationException($"{entry.dll} 里没有类型 {entry.type}");
            var m = target.Methods.FirstOrDefault(x => x.Name == entry.method && x.HasBody)
                ?? throw new InvalidOperationException($"{entry.type} 里没有带方法体的 {entry.method}");
            var (apply, already) = rewrite(m);
            if (!apply && !already)
                return (false, false);
            if (apply)
            {
                string tmp = path + ".port17tmp";
                asm.Write(tmp);
                asm.Dispose();
                File.Move(tmp, path, true);
            }
            return (apply, already);
        }

        private static bool IsNulled(MethodDefinition m) =>
            m.Body.Instructions.Count == 2 &&
            m.Body.Instructions[0].OpCode == OpCodes.Ldnull &&
            m.Body.Instructions[1].OpCode == OpCodes.Ret;

        /// <summary>删掉方法体内对 Console::add/remove_CancelKeyPress 的整次订阅。Release IL 的
        /// 形态是四条：ldarg(委托目标)、ldftn、newobj 委托.ctor(object, native int)、call
        /// add/remove——四条一起走，栈保持平衡。形态不认得就抛（不动文件）。</summary>
        private static int StripConsoleCancelKey(MethodDefinition m)
        {
            var body = m.Body;
            var targets = body.Instructions.Where(i =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand is MethodReference mr &&
                mr.DeclaringType?.FullName == "System.Console" &&
                (mr.Name == "add_CancelKeyPress" || mr.Name == "remove_CancelKeyPress")).ToList();
            if (targets.Count == 0)
                return 0;
            var il = body.GetILProcessor();
            foreach (var call in targets)
            {
                var list = body.Instructions;
                int at = list.IndexOf(call);
                if (at < 3)
                    throw new InvalidOperationException($"{m.FullName}: 订阅序列不在预期形态（太靠前）\n" +
                        string.Join("\n", list.Take(8).Select((x, i) => $"  [{i}] {x.OpCode} {x.Operand}")));
                var newobj = list[at - 1];
                var ldftn = list[at - 2];
                var target = list[at - 3];
                if (newobj.OpCode != OpCodes.Newobj || ldftn.OpCode != OpCodes.Ldftn ||
                    !IsArgPush(target.OpCode))
                    throw new InvalidOperationException(
                        $"{m.FullName}: 订阅序列不在预期形态\n" +
                        string.Join("\n", list.Skip(Math.Max(0, at - 4)).Take(5).Select((x, i) => $"  [{Math.Max(0, at - 4) + i}] {x.OpCode} {x.Operand}")));
                il.Remove(target);
                il.Remove(ldftn);
                il.Remove(newobj);
                il.Remove(call);
            }
            return targets.Count;
        }

        private static bool IsArgPush(Mono.Cecil.Cil.OpCode op) =>
            op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 ||
            op == OpCodes.Ldarg_3 || op == OpCodes.Ldarg_S || op == OpCodes.Ldarga_S ||
            op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 ||
            op == OpCodes.Ldloc_3 || op == OpCodes.Ldloc_S;
    }
}
