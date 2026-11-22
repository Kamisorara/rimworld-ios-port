// 平台改写器（安卓 patch/Patch.cs 的 iOS 雏形）：在 PC 上对 game 副本跑，iOS 的每一面
// 硬墙在这里各成一个改写条目——安卓那套实测可行的路线，同一副骨架。
//
// 用法：dotnet run --project scripts/patch-platform -- <game目录>
// 幂等：改写先探测目标方法已是改后形态就跳过；全部成功写 platform.port17 戳
// （pack-game-zip.py 打包前会自动跑本工具与 patch-os-detect.py）。
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
        // ---- 改写表：一个条目 = 一面 iOS 硬墙 ----
        // Waylith.dll: ProcessLifetime.RegisterPosixSignal → return null。
        // iOS 的 .NET 构建里 PosixSignalRegistration.Create 是 PlatformNotSupportedException
        // （真机 2026-11-22 首跑撞的第一面 Cecil 墙）；安卓把它替换成自家的 sigaction 注册，
        // iOS 连信号交互都没有（没有 SIGINT/SIGTERM 的来处），null 正是 Windows 一直走的
        // 路径（字段是 PosixSignalRegistration?，Dispose 全部 ?.）。
        private static readonly (string dll, string type, string method)[] NullReturns =
        {
            ("Waylith.dll", "Waylith.Diagnostics.ProcessLifetime", "RegisterPosixSignal"),
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
            foreach (var (dll, type, method) in NullReturns)
            {
                string path = Path.Combine(gameDir, dll);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"{dll}: 不在（这个目录里没有它）");
                    continue;
                }
                // 安卓 Patch.cs 的读取配方（实测可行）：Immediate 全解析 + 依赖目录（不用 InMemory——
                // Cecil 0.11.x 的 InMemory 路径在写出时会踩 PatchRawCode 的空引用）
                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(gameDir);
                using var asm = AssemblyDefinition.ReadAssembly(path,
                    new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
                var target = asm.MainModule.Types.FirstOrDefault(t => t.FullName == type)
                    ?? throw new InvalidOperationException($"{dll} 里没有类型 {type}");
                var m = target.Methods.FirstOrDefault(x =>
                    x.Name == method && x.HasBody)
                    ?? throw new InvalidOperationException($"{type} 里没有带方法体的 {method}");
                var body = m.Body;
                bool alreadyNull = body.Instructions.Count == 2 &&
                    body.Instructions[0].OpCode == OpCodes.Ldnull &&
                    body.Instructions[1].OpCode == OpCodes.Ret;
                if (alreadyNull)
                {
                    Console.WriteLine($"{dll}: {type}::{method} 已是 return null（改过）");
                    kept++;
                    continue;
                }
                // 整体换新方法体（Clear 旧体会让 Cecil 写出时踩未解析悬空引用）
                m.Body = new MethodBody(m) { InitLocals = false, MaxStackSize = 1 };
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                Console.WriteLine($"{dll}: {type}::{method}() → return null（iOS 无 PosixSignalRegistration，信号注册让位——null 即 Windows 路径）");
                applied.Add($"{dll}:{type}::{method}=null");
                changed++;
                // 先写临时文件再替换：Write 中途任何异常都不会把原文件截成 0
                string tmp = path + ".port17tmp";
                asm.Write(tmp);
                asm.Dispose();
                File.Move(tmp, path, true);
            }
            // 戳记录"这个工具照过面"（幂等由方法体形态检测保证，不靠戳）
            var stampLines = applied.ToList();
            string stamp = Path.Combine(gameDir, "platform.port17");
            if (File.Exists(stamp))
                stampLines = File.ReadAllLines(stamp).Union(applied).ToList();
            File.WriteAllLines(stamp, stampLines);
            Console.WriteLine($"改写 {changed} 处，已是形态跳过 {kept} 处；戳 platform.port17 在");
            return 0;
        }
    }
}
