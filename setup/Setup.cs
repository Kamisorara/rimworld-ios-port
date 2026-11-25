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
//     5. WaylithClr.* 改道：引擎程序集把这些类型按"住在 System.Runtime"引用（Ludeon 定制
//        运行时 10.0.15-waylithclr 的私货），官方 11.0 没有——Bootstrap.Initialize 第一步
//        PrepatcherCompat.Install 装载字段注入层时静态构造就 TypeLoadException（m2-engine-13）。
//        引用整体改道到 bundle 里的 PortHooks.dll 替身（行为=电脑上关掉这些功能；安卓
//        patch/Patch.cs 的同款手术，全游戏+92 mod 实测姿势）。
//     6. 强制保守档：GpuProbe::Run 整体 → throw，游戏 catch 后自落 GpuPerformanceProfile
//        .Conservative（注：探针结果有磁盘缓存 gpu-profile.bin，缓存在时探针根本不跑——
//        此补丁是兜底，防将来缓存失效后重新探针）。
//     7. 贴图上传走便宜路径：CompletedChainTextureSource::get_MipCount → 返回 stored 的
//        实际 mip 数——资产包命中后的上传永远只读已存层（纯 memcpy、BC7 原样上 GPU），
//        "BC7 解码 → Kaiser 补链 → 重编码"的昂贵路径从上传不可达（m2-engine-19 栈转储
//        定案；与 slang/探针/缓存历史全脱钩）。代价：mip 层数少于元数据要求，远景画质略降。
//   v10（窗口后端反转 v2：WAYLITH_BACKEND=Android 走引擎自带 AndroidPlatform，宿主喂数——
//     启动器先行的地基；AndroidHost 的 IsInitialized/IsApplicationThread 两道门由
//     hooks/IosHost.cs 反射直写字段点亮，不在此处）：
//    9.  OperatingSystem::IsAndroid 调用点（Waylith.Windowing 全程序集，AutoCandidates 与
//        AndroidBackend.Open 两处）→ ldc.i4.1（后端选择与门 1；IsIOS 不用动——env 强制候选表）。
//   10.  AndroidPlatform 的静态字段 s_extensions 字面量 "VK_KHR_android_surface" →
//        "VK_EXT_metal_surface"（MoltenVK 的 surface 扩展；VK_KHR_surface 保持）。
//   11.  new AndroidSurfaceHandle(...) 两处（AndroidWindow.InitialSurface getter 与
//        AndroidPlatform.OnSurfaceChanged）→ new IOSSurfaceHandle(...)（同为单 nint 参数，
//        VulkanSurface.Create 的 IOS 分支 → vkCreateMetalSurfaceEXT，layer=宿主 CAMetalLayer）。
//   12.  Unity 程序集名的类型转发门面（v11；安卓 patch/Patch.cs WriteForwarders 同款）：mod 是对着
//        UnityEngine.CoreModule 等 Unity 程序集编译的，引擎用 UnityStubs.dll 顶替；Ludeon 定制运行时靠
//        AssemblyNameRedirects 改名，官方运行时的 Resolving 回调又不许拿另一个名字的程序集应答，所以走
//        标准做法——每个名字一个只含 TypeForwardedTo 的门面 dll 写进 game/（宿主下次启动把它们收进 TPA）。
//        名单 = 安卓 host/forwarders.txt（从工坊 mod 的引用数出来的 72 个）。0.4.30 真机带 mod：
//        DamageMotes / RimPawnChineseName 因缺 UnityEngine.CoreModule 门面 ReflectionTypeLoad。
//   成功写 port17-setup.v11.stamp（宿主认它决定进引擎还是先跑 setup）。
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
        private static string logPath;

        // 设备上 Console 的去向不稳（宿主的 stdout 泵偶尔接不住）：每行同时落
        // <game>/setup-log.txt，永远有据可查。
        private static void Say(string line)
        {
            Console.WriteLine(line); // 本体（批量替换输出调用时别动这一行）
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss ") + line + "\n"); }
            catch { /* 日志失败不拦改写 */ }
        }

        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Say("[setup] usage: Port17Setup.dll <game folder> [bundle folder]");
                return 2;
            }
            // v9 起第二参数 = .app bundle 目录（PortHooks.dll 在那；本地测试可以只给 game）。
            string game = args[0];
            string bundle = args.Length >= 2 ? args[1] : game;
            logPath = Path.Combine(game, "setup-log.txt");
            if (!File.Exists(Path.Combine(game, "RimWorld.dll")))
            {
                Say("[setup] no RimWorld.dll in " + game);
                return 1;
            }
            int changed = 0, kept = 0;
            try
            {
                PatchOsDetection(game, ref changed, ref kept);
                NullifyRegisterPosixSignal(game, ref changed, ref kept);
                VoidifyLifetimeHooks(game, ref changed, ref kept); // 2.5：含 3 的删行（Hook/Dispose 已整哑）
                RedirectProcessPath(game, ref changed, ref kept);
                RetargetWaylithClr(game, ref changed, ref kept);
                ForceConservativeTier(game, ref changed, ref kept);
                CheapTextureChain(game, ref changed, ref kept);
                PersistentDataUnderDocuments(game, ref changed, ref kept);
                PatchAndroidGate(game, ref changed, ref kept);
                SwapAndroidSurfaceExtension(game, ref changed, ref kept);
                RetargetAndroidSurfaceHandle(game, ref changed, ref kept);
                PortMobileSupport(game, bundle, ref changed, ref kept);
                HarmonyCompatRewrites(game, bundle, ref changed, ref kept);
                WriteUnityForwarders(game, ref changed, ref kept);
                MarkArm64(game);
            }
            catch (Exception e)
            {
                Say("[setup] failed: " + e);
                return 3;
            }
            File.WriteAllLines(Path.Combine(game, "port17-setup.v11.stamp"),
                new[] { "Port17Setup v11", "changed=" + changed, "kept=" + kept });
            Say($"[setup] done: {changed} rewritten, {kept} already");
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
            try
            {
                return AssemblyDefinition.ReadAssembly(path,
                    new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
            }
            catch (BadImageFormatException)
            {
                // 原生 dll（waylithaudio / slang / steam_api 等非托管件）不是 .NET 程序集——
                // Steam 原件目录直接跑 setup 时会撞上（打包 zip 的设备路径不含它们），跳过
                return null;
            }
        }

        private static void Save(string game, string dll, AssemblyDefinition asm)
        {
            string path = Path.Combine(game, dll);
            string tmp = path + ".p17tmp";
            asm.Write(tmp);
            asm.Dispose();
            // 写完立刻 Move 会撞杀毒扫描的锁（本地 Windows 实测，目标被占着 Delete 权）——
            // 短暂重试后退化 Copy+Delete（Copy 只要写权，扫到一半的目标也能盖）
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    File.Move(tmp, path, true);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(500);
                }
            }
            File.Copy(tmp, path, true);
            File.Delete(tmp);
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
                    Say($"[setup] {dll}: not present, skipped");
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
                        Say($"[setup] {dll}: OperatingSystem::{probe}() → true ×{did}");
                    }
                    else
                        Say($"[setup] {dll}: {probe} 已改或不在（跳过）");
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
                Say($"[setup] {dll}: not present, skipped");
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
                    Say($"[setup] {dll}: {method} 已是 return null（跳过）");
                    kept++;
                    return;
                }
                m.Body = new MethodBody(m) { InitLocals = false, MaxStackSize = 1 };
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                Save(game, dll, asm);
                Say($"[setup] {dll}: {type}::{method} → return null");
                changed++;
            }
        }

        // ---- 2.5 ProcessLifetime 的 Hook/Dispose 整体哑掉（方法体 = ret）----
        // 真机四跑：删掉 CancelKeyPress 后崩在保留的下一个订阅上——AppDomain.ProcessExit +=
        // 触发 Delegate.Combine→InternalEqualTypes→GetType 的原生 SIGSEGV（无 "Stack overflow"
        // 字样，非溢出形态）。这三个钩子只服务优雅退出（Ctrl+C/SIGTERM/收尾），iOS 没有这些
        // 交互也不影响出画面——整族绕开。void 方法：体 = 单条 ret。

        private static readonly (string dll, string type, string method)[] VoidReturns =
        {
            ("Waylith.dll", "Waylith.Diagnostics.ProcessLifetime", "Hook"),
            ("Waylith.dll", "Waylith.Diagnostics.ProcessLifetime", "Dispose"),
        };

        private static void VoidifyLifetimeHooks(string game, ref int changed, ref int kept)
        {
            const string dll = "Waylith.dll", type = "Waylith.Diagnostics.ProcessLifetime";
            var asm = Open(game, dll);
            if (asm == null)
            {
                Say($"[setup] {dll}: not present, skipped");
                return;
            }
            using (asm)
            {
                var t = FindType(asm, type) ?? throw new InvalidOperationException($"{dll}: {type} 不在");
                int did = 0;
                foreach (var (_, _, name) in VoidReturns)
                {
                    var m = t.Methods.FirstOrDefault(x => x.Name == name && x.HasBody);
                    if (m == null)
                        continue;
                    if (m.Body.Instructions.Count == 1 && m.Body.Instructions[0].OpCode == OpCodes.Ret)
                        continue; // 已哑
                    m.Body = new MethodBody(m) { InitLocals = false, MaxStackSize = 0 };
                    m.Body.GetILProcessor().Emit(OpCodes.Ret);
                    did++;
                }
                if (did > 0)
                {
                    Save(game, dll, asm);
                    Say($"[setup] {dll}: ProcessLifetime Hook/Dispose 哑化 ×{did}（优雅退出一族整体绕开）");
                    changed++;
                }
                else
                {
                    Say($"[setup] {dll}: Hook/Dispose 已哑化（跳过）");
                    kept++;
                }
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
                Say($"[setup] {dll}: not present, skipped");
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
                    Say($"[setup] {dll}: Console.CancelKeyPress 订阅删除 ×{did}");
                    changed++;
                }
                else
                {
                    Say($"[setup] {dll}: 无 CancelKeyPress 订阅（跳过）");
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

        // ---- 4. 资源包路径改道（十跑版）：Bootstrap.ConfigureRuntime 用 Environment.ProcessPath
        // 的目录找 Assets.zip——iOS 上 ProcessPath 读真实进程可执行（.app 里的宿主），与
        // host API 的 exePath 无关；v3 曾换成读环境变量，托管侧读不到（GetDirectoryName(null)
        // → Path.Combine 炸 ArgumentNullException(path1)）。本版换 CurrentDirectory（getcwd，
        // 进程级实时——宿主 chdir(game) 过必生效）：
        //   call get_ProcessPath            → call get_CurrentDirectory
        //   （v3 形态：ldstr "PORT17_GAME_DLL"; call GetEnvironmentVariable 也归位到同一目标）
        //   紧随的 call Path::GetDirectoryName → nop（栈平衡：cwd 本身就是目标目录）
        // 资源包与 RimWorld_Data 全落 game。CrashHandler 与 Waylith.dll 里的其余 ProcessPath
        // 都在非启动路径，不动。

        private static void RedirectProcessPath(string game, ref int changed, ref int kept)
        {
            const string dll = "RimWorld.dll", type = "RimWorldWaylith.Bootstrap", method = "ConfigureRuntime";
            var asm = Open(game, dll);
            if (asm == null)
            {
                Say($"[setup] {dll}: not present, skipped");
                return;
            }
            using (asm)
            {
                var t = FindType(asm, type) ?? throw new InvalidOperationException($"{dll}: {type} 不在");
                var m = t.Methods.FirstOrDefault(x => x.Name == method && x.HasBody)
                    ?? throw new InvalidOperationException($"{dll}: {type}::{method} 不在");
                var il = m.Body.GetILProcessor();
                var list = m.Body.Instructions;
                var getCwd = asm.MainModule.ImportReference(
                    typeof(Environment).GetProperty("CurrentDirectory").GetGetMethod());
                bool already = false;
                int did = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var instr = list[i];
                    if (instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Callvirt)
                        continue;
                    if (instr.Operand is not MethodReference mr || mr.DeclaringType?.FullName != "System.Environment")
                        continue;
                    if (mr.Name == "get_ProcessPath" || mr.Name == "GetEnvironmentVariable")
                    {
                        // v3 的 ldstr "PORT17_GAME_DLL" 前缀一并归位
                        if (mr.Name == "GetEnvironmentVariable" && i > 0 && list[i - 1].OpCode == OpCodes.Ldstr &&
                            (string)list[i - 1].Operand == "PORT17_GAME_DLL")
                        {
                            il.Remove(list[i - 1]);
                            i--;
                        }
                        instr.Operand = getCwd;
                        did++;
                        // 紧随的 GetDirectoryName 让位（cwd 即目录，栈上 string 原样通过）
                        if (i + 1 < list.Count && list[i + 1].Operand is MethodReference dn &&
                            dn.DeclaringType?.FullName == "System.IO.Path" && dn.Name == "GetDirectoryName")
                        {
                            il.Replace(list[i + 1], Instruction.Create(OpCodes.Nop));
                        }
                    }
                    else if (mr.Name == "get_CurrentDirectory")
                    {
                        already = true; // v4 形态在
                    }
                }
                if (did == 0 && already)
                {
                    Say($"[setup] {dll}: ConfigureRuntime 路径源已是 CurrentDirectory（跳过）");
                    kept++;
                    return;
                }
                if (did == 0)
                    throw new InvalidOperationException($"{dll}: ConfigureRuntime 里没有找到 ProcessPath/env 调用——版本变了？");
                Save(game, dll, asm);
                Say($"[setup] {dll}: ConfigureRuntime 路径源 → CurrentDirectory ×{did}（宿主 chdir 过 game）");
                changed++;
            }
        }

        // ---- 5. WaylithClr.* 引用改道 PortHooks（m2-engine-13 的墙） ----

        // Ludeon 的运行时分支（10.0.15-waylithclr）在自己的 System.Runtime 里加了一族
        // WaylithClr.* 类型：字段注入、方法体替换/变换管线、自分析器。引擎程序集按"住在
        // System.Runtime"引用它们（Version 统一到已装载的 11.0 后按类型名找，找不到就抛
        // TypeLoadException）。bundle 里的 PortHooks.dll（安卓 hooks/WaylithClr.cs 同源）带
        // 全套替身——行为=电脑上关掉这些功能：IsSupported=false、Commit 抛 Ludeon 的
        // NotSupportedException，引擎自己降级。这里把 scope=System.Runtime 的 WaylithClr.*
        // TypeRef 改道到 PortHooks（安卓 patch/Patch.cs 的同款 10 行手术）。
        // 只动元数据的解析范围，方法体一根毛都不碰。
        private static void RetargetWaylithClr(string game, ref int changed, ref int kept)
        {
            // 快筛：引用者的 #Strings 堆里必有 "WaylithClr" 字面量（命名空间串）。解释器上
            // 全量 Cecil 读 60 多个 dll 太慢，先按字节扫，含字面量的才打开。
            byte[] needle = System.Text.Encoding.UTF8.GetBytes("WaylithClr");
            int touched = 0, total = 0;
            foreach (string path in Directory.EnumerateFiles(game, "*.dll"))
            {
                string name = Path.GetFileName(path);
                // 游戏自带的定制运行时件（System.* 命名）永远轮不到引擎装载（TPA 里 bundle
                // 同名件优先），不改写它们——尤其 System.Private.CoreLib 有几十 MB，白写费时。
                if (name.StartsWith("System", StringComparison.Ordinal))
                    continue;
                if (!ContainsBytes(path, needle))
                    continue;
                AssemblyDefinition asm;
                try
                {
                    asm = Open(game, name);
                }
                catch (Exception e)
                {
                    Say("[setup] " + name + ": 打不开（" + e.GetType().Name + ": " + e.Message + "）——跳过");
                    continue;
                }
                if (asm == null)
                    continue;
                using (asm)
                {
                    var portHooks = asm.MainModule.AssemblyReferences.FirstOrDefault(r => r.Name == "PortHooks");
                    if (portHooks == null)
                    {
                        portHooks = new AssemblyNameReference("PortHooks", new Version(1, 0, 0, 0));
                        asm.MainModule.AssemblyReferences.Add(portHooks);
                    }
                    int did = 0;
                    foreach (TypeReference t in asm.MainModule.GetTypeReferences())
                    {
                        if (t.FullName.StartsWith("WaylithClr.", StringComparison.Ordinal) &&
                            t.Scope is AssemblyNameReference scope && scope.Name == "System.Runtime")
                        {
                            t.Scope = portHooks;
                            did++;
                        }
                    }
                    if (did > 0)
                    {
                        Save(game, name, asm);
                        Say($"[setup] {name}: WaylithClr.* → PortHooks ×{did}");
                        touched++;
                        total += did;
                    }
                }
            }
            changed += total;
            Say(touched > 0
                ? $"[setup] WaylithClr 改道：{touched} 个程序集 / {total} 处引用 → PortHooks.dll"
                : "[setup] WaylithClr：没有要改道的引用（已在或不存在）");
            if (touched == 0)
                kept++;
        }

        private static bool ContainsBytes(string path, byte[] needle)
        {
            byte[] hay = File.ReadAllBytes(path);
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                if (hay[i] != needle[0])
                    continue;
                int j = 1;
                while (j < needle.Length && hay[i + j] == needle[j])
                    j++;
                if (j == needle.Length)
                    return true;
            }
            return false;
        }

        // ---- 6. 强制保守档（贴图管线走快路径） ----

        // m2-engine-18 看门狗栈转储定的案：slang 可装载 → GpuProbe 成功 → 游戏选"按需上传
        // + 运行时 Kaiser mip 链"贴图管线 → KaiserMipFilter（Vector128 SIMD 重度）在解释器
        // 上慢到每贴图数分钟（主线程+渲染线程三条一起磨，帧循环冻结 >60s——十五/十六跑的
        // "卡 Initializing 死锁"假象）。而 GpuProbe 失败时游戏 catch (Exception) 自落
        // GpuPerformanceProfile.Conservative——贴图走"预生成缓存"快路径（十四跑实证：
        // 首跑 13 分钟建缓存、命中后 3 秒）。slang 本身无辜且世界视图的着色器变体编译
        // 离不开它（十四跑 Magenta.vert 之死）——所以解法是两头全要：slang 保持可装载，
        // 这里把 GpuProbe::Run 整体换成 throw，游戏自己落保守档。
        private static void ForceConservativeTier(string game, ref int changed, ref int kept)
        {
            const string dll = "Waylith.dll", type = "Waylith.Hardware.GpuProbe", method = "Run";
            var asm = Open(game, dll);
            if (asm == null)
            {
                Say($"[setup] {dll}: not present, skipped");
                return;
            }
            using (asm)
            {
                var t = FindType(asm, type) ?? throw new InvalidOperationException($"{dll}: {type} 不在");
                var runs = t.Methods.Where(x => x.Name == method && x.HasBody).ToList();
                if (runs.Count != 1)
                    throw new InvalidOperationException($"{dll}: {type}::{method} 有 {runs.Count} 个带体重载（期望 1）——版本变了，不动");
                var m = runs[0];
                bool thrown = m.Body.Instructions.Count == 3 &&
                    m.Body.Instructions[0].OpCode == OpCodes.Ldstr &&
                    m.Body.Instructions[1].OpCode == OpCodes.Newobj &&
                    m.Body.Instructions[2].OpCode == OpCodes.Throw;
                if (thrown)
                {
                    Say($"[setup] {dll}: GpuProbe::Run 已是强制抛出（跳过）");
                    kept++;
                    return;
                }
                m.Body = new MethodBody(m) { InitLocals = false, MaxStackSize = 1 };
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldstr, "port17: GpuProbe disabled (interpreter texture grind guard - forcing conservative tier)");
                var ctor = asm.MainModule.ImportReference(
                    typeof(NotSupportedException).GetConstructor(new[] { typeof(string) }));
                il.Emit(OpCodes.Newobj, ctor);
                il.Emit(OpCodes.Throw);
                Save(game, dll, asm);
                Say($"[setup] {dll}: GpuProbe::Run → throw（游戏自落保守档：贴图走预生成缓存快路径，slang 留给世界视图着色器）");
                changed++;
            }
        }

        // ---- 7. 贴图上传走便宜路径（CompletedChainTextureSource 只暴露 stored 的 mip 数） ----

        // m2-engine-19 看门狗栈转储的续案：贴图上传命中资产包后，若挂载点元数据要求的
        // mip 数多于包文件自带的，TryOpenPackTexture 会包一层 CompletedChainTextureSource，
        // 读取高层 mip 时现场做"BC7 解码 → RGBA32 → Kaiser 补链 → 重编码回 BC7"——SIMD
        // 重度四连，解释器上每贴图数分钟、三条线程一起磨、帧循环冻结（十五/十六跑的
        // "卡 Initializing"）。这个补丁把包装器的 get_MipCount 改成返回 stored（包内源）
        // 的实际 mip 数：BuildCacheUploads 只排 stored 层、每层走 PackedTextureSource 的
        // 纯 memcpy，昂贵的 Chain() 从上传路径不可达——BC7 原样上 GPU（M4 原生支持），
        // 与 slang/探针/缓存历史全脱钩。代价：贴图 mip 层数少于元数据要求（远景画质
        // 略降），可玩性优先。
        private static void CheapTextureChain(string game, ref int changed, ref int kept)
        {
            const string dll = "Waylith.dll";
            const string type = "Waylith.Graphics.Textures.CompletedChainTextureSource";
            const string iface = "Waylith.Graphics.Textures.ITextureCacheSource";
            var asm = Open(game, dll);
            if (asm == null)
            {
                Say($"[setup] {dll}: not present, skipped");
                return;
            }
            using (asm)
            {
                var t = FindType(asm, type) ?? throw new InvalidOperationException($"{dll}: {type} 不在");
                var m = t.Methods.FirstOrDefault(x => x.Name == "get_MipCount" && x.HasBody)
                    ?? throw new InvalidOperationException($"{dll}: {type}::get_MipCount 不在——版本变了？");
                bool patched = m.Body.Instructions.Count == 4 &&
                    m.Body.Instructions[0].OpCode == OpCodes.Ldarg_0 &&
                    m.Body.Instructions[1].OpCode == OpCodes.Ldfld &&
                    m.Body.Instructions[2].OpCode == OpCodes.Callvirt &&
                    m.Body.Instructions[3].OpCode == OpCodes.Ret;
                if (patched)
                {
                    Say($"[setup] {dll}: CompletedChainTextureSource::get_MipCount 已是 stored 直通（跳过）");
                    kept++;
                    return;
                }
                // 主构造捕获字段的真名是 "<stored>P"（ilspy 展示成参数名 stored）
                var fStored = t.Fields.FirstOrDefault(f => f.Name == "<stored>P" || f.Name == "stored")
                    ?? throw new InvalidOperationException($"{dll}: {type} 缺 stored 字段——版本变了？");
                var iT = FindType(asm, iface) ?? throw new InvalidOperationException($"{dll}: {iface} 不在");
                var iGet = iT.Methods.FirstOrDefault(x => x.Name == "get_MipCount")
                    ?? throw new InvalidOperationException($"{dll}: {iface}::get_MipCount 不在");
                m.Body = new MethodBody(m) { InitLocals = false, MaxStackSize = 1 };
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fStored);
                il.Emit(OpCodes.Callvirt, iGet);
                il.Emit(OpCodes.Ret);
                Save(game, dll, asm);
                Say($"[setup] {dll}: CompletedChainTextureSource::get_MipCount → stored.MipCount（上传纯 memcpy，昂贵补链不可达）");
                changed++;
            }
        }

        // ---- 8. 持久数据根改道 Documents（真容器容器根禁写） ----

        // m2-engine-23 定案：GetLocalAppDataLow 的 Windows 兜底组出 home/AppData/LocalLow
        // （shell32 替身必然失败 → 必走此路）。LiveContainer 的"家"是嵌套普通文件夹、根
        // 可写 → 一直没炸；SideStore 直装的家是真沙盒容器，现代 iOS 禁止在容器根新建
        // 目录 → EPERM → "Critical error in root Start()" → 全黑 + NullReferenceException
        // 连环。改道：ldstr "AppData" → "Documents/AppData"，组合出
        // home/Documents/AppData/LocalLow——Documents 在两种环境都可写。旧安装（LC）的
        // 存档在老家 AppData/LocalLow 下，升级后要迁移的手工拷到新位置。
        private static void PersistentDataUnderDocuments(string game, ref int changed, ref int kept)
        {
            const string dll = "RimWorld.dll", type = "RimWorldWaylith.Bootstrap", method = "GetLocalAppDataLow";
            var asm = Open(game, dll);
            if (asm == null)
            {
                Say($"[setup] {dll}: not present, skipped");
                return;
            }
            using (asm)
            {
                var t = FindType(asm, type) ?? throw new InvalidOperationException($"{dll}: {type} 不在");
                var m = t.Methods.FirstOrDefault(x => x.Name == method && x.HasBody)
                    ?? throw new InvalidOperationException($"{dll}: {type}::{method} 不在——版本变了？");
                if (m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "Documents/AppData"))
                {
                    Say($"[setup] {dll}: GetLocalAppDataLow 已在 Documents 下（跳过）");
                    kept++;
                    return;
                }
                var hits = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "AppData").ToList();
                if (hits.Count != 1)
                    throw new InvalidOperationException($"{dll}: {method} 里 \"AppData\" 字面量有 {hits.Count} 处（期望 1）——版本变了，不动");
                hits[0].Operand = "Documents/AppData";
                Save(game, dll, asm);
                Say($"[setup] {dll}: GetLocalAppDataLow → Documents/AppData/LocalLow（真容器根禁写的正解）");
                changed++;
            }
        }

        // ---- v10-9. OperatingSystem::IsAndroid 调用点 → true（窗口后端反转 v2） ----

        // Waylith.Windowing 全程序集的 IsAndroid 调用点（ilspy 核实两处：AutoCandidates 的
        // 候选表分支、AndroidBackend.Open 的门 1）全改 ldc.i4.1——WAYLITH_BACKEND=Android
        // 时 env 候选表不走 AutoCandidates，Open 的门必须真开。多命中允许（语义一致：
        // 本进程就是"跑安卓后端的宿主"）。
        private static void PatchAndroidGate(string game, ref int changed, ref int kept)
        {
            const string dll = "Waylith.Windowing.dll";
            var asm = Open(game, dll);
            if (asm == null)
            {
                Say($"[setup] {dll}: not present, skipped");
                return;
            }
            using (asm)
            {
                int did = 0;
                foreach (var m in AllMethods(asm))
                {
                    var il = m.Body.GetILProcessor();
                    var hits = m.Body.Instructions.Where(i =>
                        (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                        i.Operand is MethodReference mr &&
                        mr.DeclaringType?.FullName == "System.OperatingSystem" &&
                        mr.Name == "IsAndroid").ToList();
                    foreach (var call in hits)
                    {
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
                    Say($"[setup] {dll}: OperatingSystem::IsAndroid() → true ×{did}（AndroidBackend 门 1 开）");
                    changed += did;
                }
                else
                {
                    Say($"[setup] {dll}: IsAndroid 已改或不在（跳过）");
                    kept++;
                }
            }
        }

        // ---- v10-10. Vulkan 实例扩展换 Metal 面（AndroidPlatform 的静态扩展表） ----

        // s_extensions = { "VK_KHR_surface", "VK_KHR_android_surface" } 在 AndroidPlatform 的
        // .cctor 里；MoltenVK 没有 android surface 扩展，IOS 面走 VK_EXT_metal_surface——
        // ldstr 字面量原位换（patch 8 的 AppData 同款姿势）。
        private static void SwapAndroidSurfaceExtension(string game, ref int changed, ref int kept)
        {
            const string dll = "Waylith.Windowing.dll";
            const string type = "Waylith.Windowing.Backends.Android.AndroidPlatform";
            var asm = Open(game, dll);
            if (asm == null)
                return; // PatchAndroidGate 已报缺席
            using (asm)
            {
                var t = FindType(asm, type) ?? throw new InvalidOperationException($"{dll}: {type} 不在");
                var cctor = t.Methods.FirstOrDefault(x => x.Name == ".cctor" && x.HasBody)
                    ?? throw new InvalidOperationException($"{dll}: {type}::.cctor 不在——版本变了？");
                var hits = cctor.Body.Instructions
                    .Where(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "VK_KHR_android_surface").ToList();
                if (hits.Count == 0)
                {
                    bool already = cctor.Body.Instructions
                        .Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "VK_EXT_metal_surface");
                    if (already)
                    {
                        Say($"[setup] {dll}: s_extensions 已是 metal_surface（跳过）");
                        kept++;
                        return;
                    }
                    throw new InvalidOperationException($"{dll}: {type}::.cctor 里没有 android_surface 字面量——版本变了");
                }
                if (hits.Count != 1)
                    throw new InvalidOperationException($"{dll}: android_surface 字面量 {hits.Count} 处（期望 1）——版本变了");
                hits[0].Operand = "VK_EXT_metal_surface";
                Save(game, dll, asm);
                Say($"[setup] {dll}: RequiredVulkanInstanceExtensions → surface + metal_surface（MoltenVK 面）");
                changed++;
            }
        }

        // ---- v10-11. AndroidSurfaceHandle → IOSSurfaceHandle（两处 newobj 原位换） ----

        // AndroidWindow.InitialSurface getter 与 AndroidPlatform.OnSurfaceChanged 各构造一次
        // AndroidSurfaceHandle(nint)；IOSSurfaceHandle(nint MetalLayer) 同为单 nint 参数——
        // newobj 操作数原位换，栈形不变。VulkanSurface.Create 的 IOS 分支拿 Layer 走
        // vkCreateMetalSurfaceEXT（decomp 5790 的 MaOSSurfaceHandle/IOSSurfaceHandle 合并分支）。
        private static void RetargetAndroidSurfaceHandle(string game, ref int changed, ref int kept)
        {
            const string dll = "Waylith.Windowing.dll";
            const string from = "Waylith.Windowing.AndroidSurfaceHandle";
            const string to = "Waylith.Windowing.IOSSurfaceHandle";
            var asm = Open(game, dll);
            if (asm == null)
                return;
            using (asm)
            {
                var toType = FindType(asm, to) ?? throw new InvalidOperationException($"{dll}: {to} 不在");
                var toCtor = toType.Methods.FirstOrDefault(x => x.IsConstructor && x.Parameters.Count == 1)
                    ?? throw new InvalidOperationException($"{dll}: {to} 缺单参 .ctor");
                int did = 0;
                foreach (var t in asm.MainModule.Types)
                {
                    foreach (var m in t.Methods)
                    {
                        if (!m.HasBody)
                            continue;
                        foreach (var i in m.Body.Instructions)
                        {
                            if (i.OpCode == OpCodes.Newobj && i.Operand is MethodReference mr &&
                                mr.DeclaringType?.FullName == from)
                            {
                                i.Operand = asm.MainModule.ImportReference(toCtor);
                                did++;
                            }
                        }
                    }
                }
                if (did > 0)
                {
                    Save(game, dll, asm);
                    Say($"[setup] {dll}: new AndroidSurfaceHandle → IOSSurfaceHandle ×{did}（vkCreateMetalSurfaceEXT 面）");
                    changed += did;
                }
                else
                {
                    Say($"[setup] {dll}: AndroidSurfaceHandle 构造已改或不在（跳过）");
                    kept++;
                }
            }
        }

        // ---- 9. 机器类型 AMD64 → ARM64（原地 2 字节，幂等） ----

        /// <summary>
        /// The Unity assembly names Workshop mods were compiled against; the engine's UnityStubs.dll stands in for all of
        /// them (the list of the Android port's host/forwarders.txt, counted from Workshop mods with patch.dll --refs).
        /// </summary>
        private static readonly string[] UnityForwarderNames =
        {
            "Assembly-CSharp-firstpass", "Unity.Burst.Unsafe", "Unity.Burst", "Unity.Collections.LowLevel.ILSupport",
            "Unity.Collections", "Unity.Mathematics", "Unity.TextMeshPro", "UnityEngine.AIModule",
            "UnityEngine.ARModule", "UnityEngine.AccessibilityModule", "UnityEngine.AndroidJNIModule",
            "UnityEngine.AnimationModule", "UnityEngine.AssetBundleModule", "UnityEngine.AudioModule",
            "UnityEngine.ClothModule", "UnityEngine.ClusterInputModule", "UnityEngine.ClusterRendererModule",
            "UnityEngine.ContentLoadModule", "UnityEngine.CoreModule", "UnityEngine.CrashReportingModule",
            "UnityEngine.DSPGraphModule", "UnityEngine.DirectorModule", "UnityEngine.GIModule",
            "UnityEngine.GameCenterModule", "UnityEngine.GridModule", "UnityEngine.HotReloadModule",
            "UnityEngine.IMGUIModule", "UnityEngine.ImageConversionModule", "UnityEngine.InputLegacyModule",
            "UnityEngine.InputModule", "UnityEngine.JSONSerializeModule", "UnityEngine.LocalizationModule",
            "UnityEngine.NVIDIAModule", "UnityEngine.ParticleSystemModule", "UnityEngine.ParticlesLegacyModule",
            "UnityEngine.PerformanceReportingModule", "UnityEngine.Physics2DModule", "UnityEngine.PhysicsModule",
            "UnityEngine.ProfilerModule", "UnityEngine.PropertiesModule", "UnityEngine.RuntimeInitializeOnLoadManagerInitializerModule",
            "UnityEngine.ScreenCaptureModule", "UnityEngine.SharedInternalsModule", "UnityEngine.SpriteMaskModule",
            "UnityEngine.SpriteShapeModule", "UnityEngine.StreamingModule", "UnityEngine.SubsystemsModule",
            "UnityEngine.TLSModule", "UnityEngine.TerrainModule", "UnityEngine.TerrainPhysicsModule",
            "UnityEngine.TextCoreFontEngineModule", "UnityEngine.TextCoreModule", "UnityEngine.TextCoreTextEngineModule",
            "UnityEngine.TextRenderingModule", "UnityEngine.TilemapModule", "UnityEngine.UI",
            "UnityEngine.UIElementsModule", "UnityEngine.UIModule", "UnityEngine.UmbraModule",
            "UnityEngine.UnityAnalyticsModule", "UnityEngine.UnityConnectModule", "UnityEngine.UnityCurlModule",
            "UnityEngine.UnityTestProtocolModule", "UnityEngine.UnityWebRequestAssetBundleModule",
            "UnityEngine.UnityWebRequestAudioModule", "UnityEngine.UnityWebRequestModule",
            "UnityEngine.UnityWebRequestTextureModule", "UnityEngine.UnityWebRequestWWWModule",
            "UnityEngine.VFXModule", "UnityEngine.VRModule", "UnityEngine.VehiclesModule", "UnityEngine.VideoModule",
            "UnityEngine.VirtualTexturingModule", "UnityEngine.WindModule", "UnityEngine.XRModule", "UnityEngine",
        };

        /// <summary>
        /// Type-forwarding facades for the Unity assembly names (patch/Patch.cs WriteForwarders, the Android port): an
        /// assembly of each name whose every public type of UnityStubs.dll is forwarded to it. Written into the game folder,
        /// which the host's TPA takes in whole; all there already = kept.
        /// </summary>
        private static void WriteUnityForwarders(string game, ref int changed, ref int kept)
        {
            string stubsPath = Path.Combine(game, "UnityStubs.dll");
            if (!File.Exists(stubsPath))
            {
                Say("[setup] no UnityStubs.dll in the game folder; Unity forwarders not written");
                return;
            }
            int missing = UnityForwarderNames.Count(n => !File.Exists(Path.Combine(game, n + ".dll")));
            if (missing == 0)
            {
                kept += UnityForwarderNames.Length;
                Say("[setup] Unity forwarders: " + UnityForwarderNames.Length + " already there");
                return;
            }
            using (AssemblyDefinition stubs = AssemblyDefinition.ReadAssembly(stubsPath,
                new ReaderParameters { ReadingMode = ReadingMode.Deferred, InMemory = true }))
            {
                var publicTypes = stubs.MainModule.Types.Where(t => t.IsPublic).ToList();
                var scope = new AssemblyNameReference(stubs.Name.Name, stubs.Name.Version);
                foreach (string name in UnityForwarderNames)
                {
                    string target = Path.Combine(game, name + ".dll");
                    if (File.Exists(target))
                    {
                        kept++;
                        continue;
                    }
                    var assemblyName = new AssemblyNameDefinition(name, new Version(0, 0, 0, 0));
                    using (AssemblyDefinition facade = AssemblyDefinition.CreateAssembly(assemblyName, name + ".dll", ModuleKind.Dll))
                    {
                        ModuleDefinition module = facade.MainModule;
                        module.AssemblyReferences.Add(scope);
                        int count = 0;
                        foreach (TypeDefinition type in publicTypes)
                        {
                            count += ForwardUnityType(module, scope, type, null);
                        }
                        facade.Write(target);
                        changed++;
                        Say("[setup] " + name + ".dll: " + count + " types forwarded to " + scope.Name);
                    }
                }
            }
        }

        private static int ForwardUnityType(ModuleDefinition module, AssemblyNameReference scope, TypeDefinition type, ExportedType declaring)
        {
            var exported = new ExportedType(declaring == null ? type.Namespace : null, type.Name, module, scope)
            {
                IsForwarder = true,
                DeclaringType = declaring,
            };
            module.ExportedTypes.Add(exported);
            int count = 1;
            foreach (TypeDefinition nested in type.NestedTypes)
            {
                if (nested.IsNestedPublic)
                {
                    count += ForwardUnityType(module, scope, nested, exported);
                }
            }
            return count;
        }

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
                    Say($"[setup] {Path.GetFileName(path)}: AMD64 -> ARM64");
                }
                catch (IOException)
                {
                    // 打不开的（锁定/权限）跳过——宿主侧还有一道同样的补写
                }
            }
            if (changed == 0)
                Say("[setup] 机器类型：没有要改的（0 或已改）");
        }
        // ---- v9：PortMobile 触控层支撑（安卓 patch/Patch.cs 通用改写的 iOS 移植）----
        // 安卓专属项不移植（内核差异，各有 iOS 对应或不存在）：
        //   monomodc P/Invoke 重指（bionic 垫片——iOS libc 直接可用）、
        //   CreateNativeExceptionHelper→libmm_exhelper.so（glibc _Unwind——CoreCLR 自有 EH）、
        //   StringRewrites（/system/fonts 安卓字体路径——iOS 字体映射另行处理）、
        //   Rewrites 表的 IsLinux/IsOSPlatform/RegisterPosixSignal/AddCancelKeyPress（setup
        //   1-3 号补丁已按常数覆盖同一批调用点，双改会互踩）、
        //   FlushAfterPatchData（Mono 已 JIT 代码的缓存冲洗——setup 先于引擎首启，无此 concerns）、
        //   WaylithClr 重指（v8 的 RetargetWaylithClr 同一操作，幂等跳过）。

        // 方法开头插入（EntryCalls）与调用点改道（ScopedRewrites/HarmonyRewrites）的表，
        // 与安卓 patch/Patch.cs 逐条对齐；目标方法全在 PortHooks.PortPlatform。
        private static readonly string[] EntryCalls =
        {
            "System.Void Verse.UIRoot_Entry::Init()",
        };

        private static readonly System.Collections.Generic.Dictionary<string, string> PortMobileEntryMap =
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["System.Void Verse.UIRoot_Entry::Init()"] = "OnEntryUIInit",
            };

        // 方法全名 → （被调方法全名 → PortPlatform 替身）。安卓 ScopedRewrites 的三条：
        // 大小写不敏感文件系统的资源解析（iOS 容器区分大小写，照搬无害）、
        // 主菜单的 Steam 初始化探测（iOS 无 Steam，返回假）、
        // mod 目录扫描对不可读目录的容忍。
        private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>> ScopedRewrites =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>
            {
                ["System.String Waylith.Utils.ResourceFiles::ResolveCaseInsensitive(System.String,System.Boolean)"] =
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["Waylith.Utils.ResourceFiles/DirectoryListing Waylith.Utils.ResourceFiles::GetListing(System.String)"] = "GetListingForResolve",
                    },
                ["System.Void Verse.UIRoot_Entry::Init()"] =
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["System.Boolean Verse.Steam.SteamManager::get_Initialized()"] = "SteamInitializedForMenu",
                    },
                ["System.Void Verse.ModLister::RebuildModList()"] =
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["System.IO.DirectoryInfo[] System.IO.DirectoryInfo::GetDirectories()"] = "ReadableModFolders",
                    },
            };

        // 只在 0Harmony.dll 的 HarmonyLib.* 类型内生效：Harmony 按名找私有字段，Mono 的
        // 私有名与 CoreCLR 不同（PortPlatform.GetFieldLax——运行时 fork WaylithClr 的
        // LaxPrivateFieldNames 的替身）。
        private static readonly System.Collections.Generic.Dictionary<string, string> HarmonyRewrites =
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["System.Reflection.FieldInfo System.Type::GetField(System.String,System.Reflection.BindingFlags)"] = "GetFieldLax",
            };

        // Harmony 的补丁发射器（instance 方法）：opcode 先过 PortPlatform.LaxCallOpcode
        // （运行时 fork LaxCallvirt 的替身）。
        private static readonly System.Collections.Generic.Dictionary<string, string> CallOpcodeFilters =
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["System.Void HarmonyLib.Emitter::Emit(System.Reflection.Emit.OpCode,System.Reflection.MethodInfo)"] = "LaxCallOpcode",
                ["System.Void HarmonyLib.Emitter::EmitCall(System.Reflection.Emit.OpCode,System.Reflection.MethodInfo)"] = "LaxCallOpcode",
                ["System.Void HarmonyLib.Emitter::DynEmit(System.Reflection.Emit.OpCode,System.Object)"] = "LaxCallOpcodeForOperand",
            };

        // Harmony 的替换方法指令表发射：列表先过 NormalizeHarmonyStack（transpiler 在跳转
        // 处留在栈上的值）。
        private const string EmitCodes =
            "System.Void HarmonyLib.MethodCreatorTools::EmitCodes(HarmonyLib.MethodCreator,HarmonyLib.Emitter,System.Collections.Generic.List`1<HarmonyLib.CodeInstruction>)";

        private static MethodReference ImportPortPlatform(ModuleDefinition module, DefaultAssemblyResolver resolver,
            System.Collections.Generic.Dictionary<string, MethodReference> cache, string methodName)
        {
            if (cache.TryGetValue(methodName, out MethodReference done))
                return done;
            AssemblyDefinition hooks = resolver.Resolve(new AssemblyNameReference("PortHooks", new Version(1, 0, 0, 0)));
            TypeDefinition platform = hooks.MainModule.GetType("PortHooks.PortPlatform");
            MethodDefinition method = platform.Methods.First(m => m.Name == methodName);
            return cache[methodName] = module.ImportReference(method);
        }

        private static DefaultAssemblyResolver OpenResolver(string game, string bundle)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(game);
            if (bundle != game)
                resolver.AddSearchDirectory(bundle);
            return resolver;
        }

        // 游戏程序集逐个过：EntryCalls 插钩 + ScopedRewrites 改道 + Assembly-CSharp 的
        // static readonly 字段摘 initonly（CoreCLR 对已初始化类型拒写，Mono/Ludeon fork
        // 放行——PortMobile 写 Dialog_Options.UIScales、TimeControls.TimeButSize）+ 逐程序集
        // 的 [IgnoresAccessChecksTo]（CoreCLR 真查访问，游戏程序集互相够 internals，安卓
        // patch.dll 同款豁免）。幂等：插入前查首指令、改道前查表、豁免前查既有属性。
        private static void PortMobileSupport(string game, string bundle, ref int changed, ref int kept)
        {
            var resolver = OpenResolver(game, bundle);
            int did = 0, keptN = 0;
            foreach (string path in Directory.EnumerateFiles(game, "*.dll").OrderBy(f => f))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("System", StringComparison.Ordinal) ||   // 运行时件不碰（v8 同规）
                    name == "PortHooks.dll" || name == "PortMobile.dll")
                    continue;
                AssemblyDefinition asm;
                try
                {
                    asm = Open(game, name);
                }
                catch (Exception e)
                {
                    Say("[setup] " + name + ": 打不开（" + e.GetType().Name + ": " + e.Message + "）——跳过");
                    continue;
                }
                if (asm == null)
                    continue;
                using (asm)
                {
                    var module = asm.MainModule;
                    var imported = new System.Collections.Generic.Dictionary<string, MethodReference>();
                    int n = 0;

                    if (name == "Assembly-CSharp.dll")
                    {
                        foreach (TypeDefinition type in module.GetTypes())
                            foreach (FieldDefinition field in type.Fields)
                                if (field.IsStatic && field.IsInitOnly && !field.HasConstant)
                                {
                                    field.IsInitOnly = false;
                                    n++;
                                }
                    }

                    foreach (TypeDefinition type in module.GetTypes())
                    {
                        foreach (MethodDefinition method in type.Methods)
                        {
                            if (!method.HasBody)
                                continue;
                            if (PortMobileEntryMap.TryGetValue(method.FullName, out string entryCall))
                            {
                                MethodReference entry = ImportPortPlatform(module, resolver, imported, entryCall);
                                Instruction first = method.Body.Instructions[0];
                                if (!(first.OpCode == OpCodes.Call && first.Operand is MethodReference already && already.FullName == entry.FullName))
                                {
                                    method.Body.GetILProcessor().InsertBefore(first, Instruction.Create(OpCodes.Call, entry));
                                    n++;
                                }
                            }
                            System.Collections.Generic.Dictionary<string, string> scoped =
                                ScopedRewrites.TryGetValue(method.FullName, out var sc) ? sc : null;
                            foreach (Instruction i in method.Body.Instructions)
                            {
                                if ((i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt) || !(i.Operand is MethodReference target))
                                    continue;
                                string replacement = null;
                                if (scoped != null && scoped.TryGetValue(target.FullName, out string r1))
                                    replacement = r1;
                                if (replacement == null)
                                    continue;
                                MethodReference reference = ImportPortPlatform(module, resolver, imported, replacement);
                                i.OpCode = OpCodes.Call;
                                i.Operand = reference;
                                n++;
                            }
                        }
                    }

                    n += ExemptFromAccessChecks(asm, resolver);

                    if (n > 0)
                    {
                        try
                        {
                            Save(game, name, asm);
                        }
                        catch (Exception e)
                        {
                            Say("[setup] " + name + ": 写回失败（" + e.GetType().Name + ": " + e.Message + "）——跳过该程序集");
                            continue;
                        }
                        Say("[setup] " + name + ": PortMobile 支撑 " + n + " 处（钩/改道/initonly/豁免）");
                        did += n;
                    }
                    else
                        keptN++;
                }
            }
            changed += did;
            kept += keptN;
            Say("[setup] PortMobileSupport：" + did + " 处改写，" + keptN + " 个程序集已就绪");
        }

        // 0Harmony.dll 的 Harmony 兼容改写：GetFieldLax（按名找私有字段）、发射器的
        // opcode 过滤（方法体内插一行 opcode = LaxCallOpcode(opcode, method)）、
        // EmitCodes 的栈归一化插行。
        private static void HarmonyCompatRewrites(string game, string bundle, ref int changed, ref int kept)
        {
            var asm = Open(game, "0Harmony.dll");
            if (asm == null)
            {
                Say("[setup] 0Harmony.dll 不在游戏目录（Harmony mod 未装？）——Harmony 兼容改写跳过");
                return;
            }
            using (asm)
            {
                var resolver = OpenResolver(game, bundle);
                var module = asm.MainModule;
                var imported = new System.Collections.Generic.Dictionary<string, MethodReference>();
                int n = 0;
                foreach (TypeDefinition type in module.GetTypes())
                {
                    bool inHarmonyLib = type.FullName.StartsWith("HarmonyLib.", StringComparison.Ordinal);
                    foreach (MethodDefinition method in type.Methods)
                    {
                        if (!method.HasBody)
                            continue;
                        if (CallOpcodeFilters.TryGetValue(method.FullName, out string filterName))
                        {
                            MethodReference filter = ImportPortPlatform(module, resolver, imported, filterName);
                            Instruction first = method.Body.Instructions[0];
                            bool done = method.Body.Instructions.Count > 2 && method.Body.Instructions[2].Operand is MethodReference there && there.FullName == filter.FullName;
                            if (!done)
                            {
                                // opcode = PortPlatform.LaxCallOpcode(opcode, method);（静态两参，结果写回参数 0）
                                var il = method.Body.GetILProcessor();
                                il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
                                il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2));
                                il.InsertBefore(first, Instruction.Create(OpCodes.Call, filter));
                                il.InsertBefore(first, Instruction.Create(OpCodes.Starg_S, method.Parameters[0]));
                                n++;
                            }
                        }
                        if (method.FullName == EmitCodes)
                        {
                            MethodReference normalize = ImportPortPlatform(module, resolver, imported, "NormalizeHarmonyStack");
                            Instruction first = method.Body.Instructions[0];
                            bool done = method.Body.Instructions.Count > 2 && method.Body.Instructions[2].Operand is MethodReference there && there.FullName == normalize.FullName;
                            if (!done)
                            {
                                var il = method.Body.GetILProcessor();
                                il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
                                il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2));
                                il.InsertBefore(first, Instruction.Create(OpCodes.Call, normalize));
                                n++;
                            }
                        }
                        foreach (Instruction i in method.Body.Instructions)
                        {
                            if ((i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt) || !(i.Operand is MethodReference target))
                                continue;
                            if (!inHarmonyLib || !HarmonyRewrites.TryGetValue(target.FullName, out string replacement))
                                continue;
                            MethodReference reference = ImportPortPlatform(module, resolver, imported, replacement);
                            i.OpCode = OpCodes.Call;
                            i.Operand = reference;
                            n++;
                        }
                    }
                }
                if (n > 0)
                {
                    Save(game, "0Harmony.dll", asm);
                    Say("[setup] 0Harmony.dll: Harmony 兼容改写 " + n + " 处");
                    changed += n;
                }
                else
                {
                    Say("[setup] 0Harmony.dll: Harmony 兼容改写已就绪");
                    kept++;
                }
            }
        }

        // ---- 访问豁免（安卓 Patch.cs ExemptFromAccessChecks 的移植）----
        // CoreCLR 真查 IL 访问（Mono/Ludeon fork 有 SkipAccessChecks 开关）：游戏程序集互相
        // 够 internals、mod/Harmony 经反射够游戏 internals，逐程序集发
        // [IgnoresAccessChecksTo(其他程序集)]（.NET 10+ 运行时原生认这个属性）。

        private static bool MayName(TypeDefinition type, bool friend)
        {
            for (TypeDefinition t = type; t != null; t = t.DeclaringType)
            {
                bool visible = t.DeclaringType == null
                    ? t.IsPublic || friend
                    : t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamilyOrAssembly || (friend && (t.IsNestedAssembly || t.IsNestedFamilyAndAssembly));
                if (!visible)
                    return false;
            }
            return true;
        }

        private static bool MayUse(IMemberDefinition member, bool friend)
        {
            if (member is MethodDefinition method)
                return method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly || (friend && (method.IsAssembly || method.IsFamilyAndAssembly));
            if (member is FieldDefinition field)
                return field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly || (friend && (field.IsAssembly || field.IsFamilyAndAssembly));
            return true;
        }

        private static T TryResolve<T>(Func<T> resolve) where T : class
        {
            try { return resolve(); }
            catch { return null; }
        }

        private static int ExemptFromAccessChecks(AssemblyDefinition assembly, DefaultAssemblyResolver resolver)
        {
            string self = assembly.Name.Name;
            var friends = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.Ordinal);
            var targets = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            void Check(IMemberDefinition named, TypeDefinition type)
            {
                AssemblyDefinition target = type?.Module?.Assembly;
                if (target == null || target.Name.Name == self)
                    return;
                if (!friends.TryGetValue(target.Name.Name, out bool friend))
                {
                    friends[target.Name.Name] = friend = target.CustomAttributes.Any(a =>
                        a.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute"
                        && a.ConstructorArguments.Count == 1 && a.ConstructorArguments[0].Value is string friendName
                        && friendName.Split(',')[0].Trim() == self);
                }
                if (!(named is TypeDefinition ? MayName(type, friend) : MayUse(named, friend)))
                    targets.Add(target.Name.Name);
            }
            ModuleDefinition module = assembly.MainModule;
            try
            {
                foreach (TypeReference reference in module.GetTypeReferences())
                {
                    if (reference.Scope == module)
                        continue; // 本模块引用不过 resolver——Resolve 会把自己读进缓存并锁住稍后的写回
                    TypeDefinition type = TryResolve(reference.Resolve);
                    Check(type, type);
                }
                foreach (MemberReference reference in module.GetMemberReferences())
                {
                    if (reference.DeclaringType is ArrayType)
                        continue;
                    if (reference.DeclaringType is TypeReference dt && dt.Scope == module)
                        continue; // 同上
                    IMemberDefinition member = reference is MethodReference method ? TryResolve(method.Resolve)
                        : reference is FieldReference field ? TryResolve(field.Resolve) : null;
                    Check(member, member?.DeclaringType);
                }
            }
            catch (Exception e)
            {
                Say("[setup] " + self + ": 引用访问面看不全（" + e.GetType().Name + ": " + e.Message + "）——保持原样");
                return 0;
            }
            foreach (CustomAttribute existing in assembly.CustomAttributes)
            {
                if (existing.AttributeType.Name == "IgnoresAccessChecksToAttribute" && existing.ConstructorArguments.Count == 1)
                    targets.Remove(existing.ConstructorArguments[0].Value as string ?? "");
            }
            if (targets.Count == 0)
                return 0;
            // 属性类型本体（模块内没有才造一个）——ModPrep.IgnoreAccessChecksTo 同款。
            TypeDefinition attr = module.GetType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute");
            MethodDefinition ctor;
            if (attr == null)
            {
                TypeReference baseType = module.ImportReference(typeof(Attribute));
                attr = new TypeDefinition("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute",
                    TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, baseType);
                var field = new FieldDefinition("assemblyName", FieldAttributes.Private | FieldAttributes.InitOnly, module.TypeSystem.String);
                attr.Fields.Add(field);
                ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
                ctor.Parameters.Add(new ParameterDefinition("assemblyName", ParameterAttributes.None, module.TypeSystem.String));
                MethodReference baseCtor = module.ImportReference(typeof(Attribute).GetConstructor(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null));
                var il = ctor.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, baseCtor);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, field);
                il.Emit(OpCodes.Ret);
                attr.Methods.Add(ctor);
                module.Types.Add(attr);
            }
            else
                ctor = attr.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1);
            int added = 0;
            foreach (string name in targets.OrderBy(x => x))
            {
                var custom = new CustomAttribute(ctor);
                custom.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, name));
                assembly.CustomAttributes.Add(custom);
                added++;
            }
            return added;
        }

    }
}
