# 一次性：把 v9 的两个 patch 方法块追加进 setup/Setup.cs（类内、命名空间闭括号前）。
# 写成文件执行——工具调用层会折叠 Bash heredoc 大块文本。
import io

p = "setup/Setup.cs"
s = io.open(p, encoding="utf-8").read()

block = r"""
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
                var asm = Open(game, name);
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
                        Save(game, name, asm);
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
                    TypeDefinition type = TryResolve(reference.Resolve);
                    Check(type, type);
                }
                foreach (MemberReference reference in module.GetMemberReferences())
                {
                    if (reference.DeclaringType is ArrayType)
                        continue;
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
"""

s = s.rstrip("\n")
# 尾部是：MarkArm64 方法体闭 "        }" + 类闭 "    }" + 命名空间闭 "}"
cls_idx = s.rfind("\n    }")           # 类闭
assert cls_idx > 0, "类闭未找到"
head = s[:cls_idx]
tail = s[cls_idx:]                     # "\n    }\n}" 起
s = head + block + tail
io.open(p, "w", encoding="utf-8", newline="\n").write(s)
print("v9 方法块已写入；总行数", s.count("\n"))
