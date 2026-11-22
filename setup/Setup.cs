// Port17Setup：游戏文件的设备内改写器（安卓 PortSetup.dll 的 iOS 对应物）。
// 在 app 自己的进程里跑一次（iOS 无 fork，用掉本进程的 CoreCLR 配额，宿主随后提示重开），
// 把 PC 侧那三道补丁全部落到玩家拷进来的游戏副本上——玩家的体验从此是"Steam 文件夹
// 直接拷"，和安卓一致。
//
//   用法（宿主调）：Port17Setup.dll <game目录>
//   改写（全部幂等，全部 Cecil——与 scripts/patch-platform 同一副骨架）：
//     1. OS 探测：Brutal.Vulkan 的 Vulkan 静态构造里 OperatingSystem::IsLinux() 唯一调用
//        → ldc.i4.1（装载名 "vulkan" 落宿主前向层）；Brutal.Core.Common 的 Platform 静态
//        构造里 OperatingSystem::IsMacOS() 唯一调用 → ldc.i4.1（Darwin 家族：kqueue、
//        引擎自配 MVK 环境变量；代价的 libvulkan.1 由 bundle 附带）。
//     2. Waylith 的 ProcessLifetime::RegisterPosixSignal → return null（iOS 无
//        PosixSignalRegistration；null 即 Windows 一直走的路径）。
//     3. ProcessLifetime::Hook/Dispose 里 Console.CancelKeyPress 的订阅/退订整四条 IL 删除
//        （System.Console 在 POSIX 用同一个不支持的 API 实现 CancelKeyPress）。
//     4. 机器类型：RimWorld.dll 与 Steamworks.NET 的 AMD64 标记改 ARM64（.NET 11 实测不拒，
//        与安卓 10.0 行为不同，但改了无害且防回归）。
//   成功写 port17-setup.stamp（宿主认它决定进引擎还是先跑 setup）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Port17Setup
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("[setup] usage: Port17Setup.dll <game folder>");
                return 2;
            }
            string game = args[0];
            if (!File.Exists(Path.Combine(game, "RimWorld.dll")))
            {
                Console.WriteLine("[setup] no RimWorld.dll in " + game);
                return 1;
            }
            int changed = 0, kept = 0;
            try
            {
                PatchOsDetection(game, ref changed, ref kept);
                NullifyRegisterPosixSignal(game, ref changed, ref kept);
                StripCancelKeyPress(game, ref changed, ref kept);
                MarkArm64(game);
            }
            catch (Exception e)
            {
                Console.WriteLine("[setup] failed: " + e);
                return 3;
            }
            File.WriteAllLines(Path.Combine(game, "port17-setup.stamp"),
                new[] { "Port17Setup v1", "changed=" + changed, "kept=" + kept });
            Console.WriteLine($"[setup] done: {changed} rewritten, {kept} already");
            return 0;
        }

        // ---- 公共底座 ----

        private static AssemblyDefinition Open(string game, string dll)
        {
            string path = Path.Combine(game, dll);
            if (!File.Exists(path))
                return null;
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(game);
            return AssemblyDefinition.ReadAssembly(path,
                new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
        }

        private static void Save(string game, string dll, AssemblyDefinition asm)
        {
            string path = Path.Combine(game, dll);
            string tmp = path + ".p17tmp";
            asm.Write(tmp);
            asm.Dispose();
            File.Move(tmp, path, true);
        }

        private static TypeDefinition FindType(AssemblyDefinition asm, string fullName) =>
            asm.MainModule.Types.FirstOrDefault(t => t.FullName == fullName);

        /// <summary>dll 里的全部带体方法（含嵌套类型——ProcessLifetime 是顶层，但求稳）。</summary>
        private static IEnumerable<MethodDefinition> AllMethods(AssemblyDefinition asm)
        {
            foreach (var t in asm.MainModule.Types)
                foreach (var m in t.Methods)
                    if (m.HasBody)
                        yield return m;
        }

        // ---- 1. OS 探测 ----

        private static void PatchOsDetection(string game, ref int changed, ref int kept)
        {
            // (dll, 方法名)：在那一个方法里把 System.OperatingSystem::<方法>() 的唯一 call
            // 换成 ldc.i4.1。两个 dll 的 OS 探测调用全部位于目标静态构造里（ilspy 核实过），
            // 所以"方法内唯一命中"就是外科边界。
            var jobs = new[]
            {
                ("Brutal.Vulkan.dll", "Vulkan", "IsLinux"),
                ("Brutal.Core.Common.dll", "Platform", "IsMacOS"),
            };
            foreach (var (dll, typeName, probe) in jobs)
            {
                var asm = Open(game, dll);
                if (asm == null)
                {
                    Console.WriteLine($"[setup] {dll}: not present, skipped");
                    continue;
                }
                int did = 0;
                using (asm)
                {
                    foreach (var m in AllMethods(asm).Where(m => m.DeclaringType.Name == typeName))
                    {
                        var il = m.Body.GetILProcessor();
                        var hits = m.Body.Instructions.Where(i =>
                            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                            i.Operand is MethodReference mr &&
                            mr.DeclaringType?.FullName == "System.OperatingSystem" &&
                            mr.Name == probe).ToList();
                        if (hits.Count == 0)
                            continue;
                        if (hits.Count > 1)
                            throw new InvalidOperationException($"{dll}: {probe} 有 {hits.Count} 处调用（期望 1）——版本变了，不动");
                        foreach (var call in hits)
                        {
                            // call → ldc.i4.1 + nop×4（同宽替换不必，Cecil 重排无妨；语义一致）
                            il.InsertAfter(call, Instruction.Create(OpCodes.Ldc_I4_1));
                            var anchor = call.Next;
                            for (int n = 0; n < 3; n++)
                            {
                                il.InsertAfter(anchor, Instruction.Create(OpCodes.Nop));
                                anchor = anchor.Next;
                            }
                            il.Remove(call);
                            did++;
                        }
                    }
                    if (did > 0)
                    {
                        Save(game, dll, asm);
                        Console.WriteLine($"[setup] {dll}: OperatingSystem::{probe}() → true ×{did}");
                    }
                    else
                        Console.WriteLine($"[setup] {dll}: {probe} 已改或不在（跳过）");
                }
                if (did > 0) changed += did; else kept++;
            }
        }

        // ---- 2. RegisterPosixSignal → return null ----

        private static void NullifyRegisterPosixSignal(string game, ref int changed, ref int kept)
        {
            const string dll = "Waylith.dll", type = "Waylith.Diagnostics.ProcessLifetime", method = "RegisterPosixSignal";
            var asm = Open(game, dll);
            if (asm == null)
            {
                Console.WriteLine($"[setup] {dll}: not present, skipped");
                return;
            }
            using (asm)
            {
                var m = FindType(asm, type)?.Methods.FirstOrDefault(x => x.Name == method && x.HasBody);
                if (m == null)
                    throw new InvalidOperationException($"{dll}: {type}::{method} 不在——版本变了？");
                bool nulled = m.Body.Instructions.Count == 2 &&
                    m.Body.Instructions[0].OpCode == OpCodes.Ldnull &&
                    m.Body.Instructions[1].OpCode == OpCodes.Ret;
                if (nulled)
                {
                    Console.WriteLine($"[setup] {dll}: {method} 已是 return null（跳过）");
                    kept++;
                    return;
                }
                m.Body = new MethodBody(m) { InitLocals = false, MaxStackSize = 1 };
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                Save(game, dll, asm);
                Console.WriteLine($"[setup] {dll}: {type}::{method} → return null");
                changed++;
            }
        }

        // ---- 3. Console.CancelKeyPress 的订阅/退订删除 ----

        private static void StripCancelKeyPress(string game, ref int changed, ref int kept)
        {
            const string dll = "Waylith.dll", type = "Waylith.Diagnostics.ProcessLifetime";
            string[] methods = { "Hook", "Dispose" };
            var asm = Open(game, dll);
            if (asm == null)
            {
                Console.WriteLine($"[setup] {dll}: not present, skipped");
                return;
            }
            using (asm)
            {
                int did = 0;
                var t = FindType(asm, type) ?? throw new InvalidOperationException($"{dll}: {type} 不在");
                foreach (var name in methods)
                {
                    var m = t.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);
                    if (m == null)
                        continue;
                    did += Strip(m);
                }
                if (did > 0)
                {
                    Save(game, dll, asm);
                    Console.WriteLine($"[setup] {dll}: Console.CancelKeyPress 订阅删除 ×{did}");
                    changed++;
                }
                else
                {
                    Console.WriteLine($"[setup] {dll}: 无 CancelKeyPress 订阅（跳过）");
                    kept++;
                }
            }
        }

        /// <summary>Release IL 的四条形态：ldarg(委托目标)、ldftn、newobj ctor(object, native int)、
        /// call add/remove——整段摘除，栈平衡保持。形态不认得就抛。</summary>
        private static int Strip(MethodDefinition m)
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
                if (at < 3 ||
                    list[at - 1].OpCode != OpCodes.Newobj ||
                    list[at - 2].OpCode != OpCodes.Ldftn ||
                    !IsArgPush(list[at - 3].OpCode))
                    throw new InvalidOperationException($"{m.FullName}: 订阅序列不在预期形态");
                il.Remove(list[at - 3]);
                il.Remove(list[at - 2]);
                il.Remove(list[at - 1]);
                il.Remove(call);
            }
            return targets.Count;
        }

        private static bool IsArgPush(Mono.Cecil.Cil.OpCode op) =>
            op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 ||
            op == OpCodes.Ldarg_3 || op == OpCodes.Ldarg_S ||
            op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 ||
            op == OpCodes.Ldloc_3 || op == OpCodes.Ldloc_S;

        // ---- 4. 机器类型 AMD64 → ARM64（原地 2 字节，幂等） ----

        private static void MarkArm64(string game)
        {
            int changed = 0;
            foreach (string path in Directory.GetFiles(game, "*.dll"))
            {
                try
                {
                    using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    var head = new byte[0x40];
                    if (f.Read(head, 0, head.Length) != head.Length || head[0] != (byte)'M' || head[1] != (byte)'Z')
                        continue;
                    int pe = head[0x3c] | (head[0x3d] << 8) | (head[0x3e] << 16) | (head[0x3f] << 24);
                    f.Position = pe + 4;
                    var machine = new byte[2];
                    if (f.Read(machine, 0, 2) != 2 || machine[0] != 0x64 || machine[1] != 0x86)
                        continue; // 只认 AMD64（0x8664 小端）
                    f.Position = pe + 4;
                    f.Write(new byte[] { 0x64, 0xaa }, 0, 2); // ARM64（0xaa64 小端）
                    changed++;
                    Console.WriteLine($"[setup] {Path.GetFileName(path)}: AMD64 -> ARM64");
                }
                catch (IOException)
                {
                    // 打不开的（锁定/权限）跳过——宿主侧还有一道同样的补写
                }
            }
            if (changed == 0)
                Console.WriteLine("[setup] 机器类型：没有要改的（0 或已改）");
        }
    }
}
