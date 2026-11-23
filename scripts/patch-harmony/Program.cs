// Applies the same iOS Harmony compatibility rewrites as device setup, before the IPA is built.
//
// Pipeline: `prepare <original 0Harmony.dll> <output>` publicizes the MonoMod memory types
// (so scripts/monomod-arena can compile against them), then the default mode is run IN PLACE on
// that output with prebuilt/Port17MonoModArena.dll present: every rewrite imports from it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private const string ArenaAssemblyName = "Port17MonoModArena";
    private const string Port17ArenaType = "Port17.Port17Arena";
    private const string Port17AllocatorType = "Port17.Port17ArenaAllocator";

    private static readonly Dictionary<string, string> HarmonyRewrites = new()
    {
        ["System.Reflection.FieldInfo System.Type::GetField(System.String,System.Reflection.BindingFlags)"] = "GetFieldLax",
    };

    private static readonly (string Method, string Filter)[] CallOpcodeFilters =
    {
        ("System.Void HarmonyLib.Emitter::Emit(System.Reflection.Emit.OpCode,System.Reflection.MethodInfo)", "LaxCallOpcode"),
        ("System.Void HarmonyLib.Emitter::EmitCall(System.Reflection.Emit.OpCode,System.Reflection.MethodInfo)", "LaxCallOpcode"),
        ("System.Void HarmonyLib.Emitter::DynEmit(System.Reflection.Emit.OpCode,System.Object)", "LaxCallOpcodeForOperand"),
    };

    private const string EmitCodes =
        "System.Void HarmonyLib.MethodCreatorTools::EmitCodes(HarmonyLib.MethodCreator,HarmonyLib.Emitter,System.Collections.Generic.List`1<HarmonyLib.CodeInstruction>)";

    private static readonly string[] MemoryTypesToPublicize =
    {
        "MonoMod.Core.Platforms.IMemoryAllocator",
        "MonoMod.Core.Platforms.IAllocatedMemory",
        "MonoMod.Core.Platforms.AllocationRequest",
        "MonoMod.Core.Platforms.PositionedAllocationRequest",
    };

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "dump")
        {
            var dumped = AssemblyDefinition.ReadAssembly(args[1]);
            var dumpedDetection = dumped.MainModule.GetType("MonoMod.Utils.PlatformDetection");
            var dumpedMethod = dumpedDetection.Methods.First(candidate => candidate.Name == "DetectPlatformInfo");
            Console.WriteLine("return: " + dumpedMethod.ReturnType.FullName);
            foreach (var instruction in dumpedMethod.Body.Instructions)
                if (instruction.OpCode == OpCodes.Newobj || instruction.OpCode == OpCodes.Ret)
                    Console.WriteLine($"{instruction.Offset:X4} {instruction.OpCode} {instruction.Operand}");
            return 0;
        }

        if (args.Length == 2 && args[0] == "verify")
        {
            int count = 0;
            var asm = AssemblyDefinition.ReadAssembly(args[1]);
            foreach (var type in asm.MainModule.GetTypes())
                foreach (var method in type.Methods)
                    if (method.HasBody)
                        foreach (var instruction in method.Body.Instructions)
                            if (instruction.Operand is MethodReference target &&
                                target.DeclaringType.FullName == "PortHooks.PortPlatform")
                                count++;
            Console.WriteLine(count);
            return 0;
        }

        if (args.Length == 3 && args[0] == "extract-exhelper")
        {
            var harmony = AssemblyDefinition.ReadAssembly(args[1]);
            var resource = harmony.MainModule.Resources
                .OfType<EmbeddedResource>()
                .First(candidate => candidate.Name == "exhelper_macos_arm64.dylib");
            using (var stream = resource.GetResourceStream())
            using (var helperOutput = File.Create(args[2]))
                stream.CopyTo(helperOutput);
            Console.WriteLine(args[2]);
            return 0;
        }

        if (args.Length == 3 && args[0] == "prepare")
        {
            var original = AssemblyDefinition.ReadAssembly(args[1]);
            using (original)
            {
                int changed = PublicizeMemoryTypes(original.MainModule);
                original.Write(args[2]);
                Console.WriteLine($"prepare: {changed} MonoMod memory types publicized -> {args[2]}");
            }
            return 0;
        }

        // Executes the rewritten methods on this machine's runtime. Hand-written IL can pass
        // Cecil and decompile cleanly yet still be rejected by the JIT (0.4.10 shipped a `ret`
        // inside a try/catch and died on device with InvalidProgramException).
        if (args.Length == 2 && args[0] == "smoke")
        {
            string harmonyPath = Path.GetFullPath(args[1]);
            string folder = Path.GetDirectoryName(harmonyPath)!;
            // A fake arena: one plain RW buffer plays both the RX and the RW alias role.
            int size = Environment.SystemPageSize * 64;
            nint buffer = Marshal.AllocHGlobal(size);
            string hex = ((ulong)buffer).ToString("x");
            Environment.SetEnvironmentVariable("PORT17_JIT_RX", hex);
            Environment.SetEnvironmentVariable("PORT17_JIT_RW", hex);
            Environment.SetEnvironmentVariable("PORT17_JIT_SIZE", "0");
            Environment.SetEnvironmentVariable("PORT17_MM_RX", hex);
            Environment.SetEnvironmentVariable("PORT17_MM_RW", hex);
            Environment.SetEnvironmentVariable("PORT17_MM_SIZE", ((ulong)size).ToString("x"));

            var arenaAssembly = Assembly.LoadFrom(Path.Combine(folder, ArenaAssemblyName + ".dll"));
            Console.WriteLine("smoke: arena allocator self test: "
                + arenaAssembly.GetType(Port17ArenaType, true)!.GetMethod("SelfTest")!.Invoke(null, null));

            var harmonyAssembly = Assembly.LoadFrom(harmonyPath);
            var runtime = harmonyAssembly.GetType("MonoMod.Core.Platforms.Runtimes.Core110Runtime", true)!;
            var runtimeInstance = RuntimeHelpers.GetUninitializedObject(runtime);
            var jitPath = (string)runtime.GetMethod("GetClrJitPath", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(runtimeInstance, null)!;
            Console.WriteLine("smoke: GetClrJitPath() -> " + jitPath);

            var system = harmonyAssembly.GetType("MonoMod.Core.Platforms.Systems.MacOSSystem", true)!;
            var instance = RuntimeHelpers.GetUninitializedObject(system);
            var archType = harmonyAssembly.GetTypes().First(candidate => candidate.Name == "Arm64Arch"
                && candidate.GetInterfaces().Any(face => face.Name == "IArchitecture"));
            system.GetField("arch", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(
                instance, RuntimeHelpers.GetUninitializedObject(archType));
            var helper = system.GetMethod("CreateNativeExceptionHelper", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(instance, null);
            Console.WriteLine("smoke: CreateNativeExceptionHelper() -> " + (helper == null ? "null (expected off-device)" : helper.GetType().Name));
            Console.WriteLine("smoke: 0Harmony references " + ArenaAssemblyName + ": "
                + harmonyAssembly.GetReferencedAssemblies().Any(reference => reference.Name == ArenaAssemblyName));
            return 0;
        }

        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: Port17PatchHarmony <0Harmony.dll> <output folder> <PortHooks.dll>");
            Console.Error.WriteLine("       Port17PatchHarmony prepare <original 0Harmony.dll> <output 0Harmony.dll>");
            Console.Error.WriteLine("       Port17PatchHarmony smoke <0Harmony.dll>");
            return 2;
        }

        string source = Path.GetFullPath(args[0]);
        string outputFolder = Path.GetFullPath(args[1]);
        string hooks = Path.GetFullPath(args[2]);
        string arenaPath = Path.Combine(outputFolder, ArenaAssemblyName + ".dll");
        Directory.CreateDirectory(outputFolder);
        string output = Path.Combine(outputFolder, "0Harmony.dll");
        string reading = source;
        bool inPlace = string.Equals(source, output, StringComparison.OrdinalIgnoreCase);
        if (inPlace)
        {
            reading = output + ".source.p17tmp";
            File.Copy(source, reading, true);
        }
        if (!File.Exists(arenaPath))
        {
            Console.Error.WriteLine($"::error::{ArenaAssemblyName}.dll is missing next to the output ({arenaPath}); build scripts/monomod-arena against the prepared 0Harmony.dll first");
            return 1;
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(outputFolder);
        resolver.AddSearchDirectory(Path.GetDirectoryName(hooks)!);
        var assembly = AssemblyDefinition.ReadAssembly(reading, new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadingMode = ReadingMode.Immediate,
        });
        var arena = AssemblyDefinition.ReadAssembly(arenaPath);

        try
        {
        using (assembly)
        {
            var imported = new Dictionary<string, MethodReference>();
            var arenaModule = new ArenaModule(assembly.MainModule, arena);
            int changed = PublicizeMemoryTypes(assembly.MainModule);
            foreach (var type in assembly.MainModule.GetTypes())
            {
                bool inHarmonyLib = type.FullName.StartsWith("HarmonyLib.", StringComparison.Ordinal);
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;

                    foreach (var (methodName, filterName) in CallOpcodeFilters)
                    {
                        if (method.FullName != methodName)
                            continue;
                        var portPlatform = ImportPortPlatform(assembly.MainModule, resolver, imported, filterName);
                        var first = method.Body.Instructions[0];
                        bool done = method.Body.Instructions.Count > 2 &&
                            method.Body.Instructions[2].Operand is MethodReference already &&
                            already.FullName == portPlatform.FullName;
                        if (!done)
                        {
                            var il = method.Body.GetILProcessor();
                            il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
                            il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2));
                            il.InsertBefore(first, Instruction.Create(OpCodes.Call, portPlatform));
                            il.InsertBefore(first, Instruction.Create(OpCodes.Starg_S, method.Parameters[0]));
                            changed++;
                        }
                    }

                    if (method.FullName == EmitCodes)
                    {
                        var normalize = ImportPortPlatform(assembly.MainModule, resolver, imported, "NormalizeHarmonyStack");
                        var first = method.Body.Instructions[0];
                        bool done = method.Body.Instructions.Count > 2 &&
                            method.Body.Instructions[2].Operand is MethodReference already &&
                            already.FullName == normalize.FullName;
                        if (!done)
                        {
                            var il = method.Body.GetILProcessor();
                            il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
                            il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2));
                            il.InsertBefore(first, Instruction.Create(OpCodes.Call, normalize));
                            changed++;
                        }
                    }

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if ((instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) ||
                            instruction.Operand is not MethodReference target ||
                            !inHarmonyLib || !HarmonyRewrites.TryGetValue(target.FullName, out string replacement))
                            continue;
                        instruction.OpCode = OpCodes.Call;
                        instruction.Operand = ImportPortPlatform(assembly.MainModule, resolver, imported, replacement);
                        changed++;
                    }
                }
            }

            changed += UseBundleExHelper(assembly.MainModule, arenaModule);
            changed += UseStaticJitShim(assembly.MainModule, arenaModule);
            changed += UseArenaAllocator(assembly.MainModule, arenaModule);
            changed += MakePatchDataAliasAware(assembly.MainModule, arenaModule);
            changed += SwapJitVtableInsteadOfPatching(assembly.MainModule, arenaModule);
            changed += UseRuntimeMethodHandleEntryPoint(assembly.MainModule, arenaModule);
            changed += DropRuntimeRwAlias(assembly.MainModule);
            changed += PatchCore110VtableCount(assembly.MainModule);
            changed += ForcePlatformTriple(assembly.MainModule);

            string temp = output + ".p17tmp";
            assembly.Write(temp);
            File.Move(temp, output, true);
            Console.WriteLine($"0Harmony.dll: {changed} rewrites -> {output}");
        }
        }
        finally
        {
            // A failed in-place pass must not leave the .source.p17tmp copy behind.
            if (inPlace && File.Exists(reading))
                File.Delete(reading);
        }
        if (inPlace)
            File.Delete(reading);
        return 0;
    }

    private static int PublicizeMemoryTypes(ModuleDefinition module)
    {
        int changed = 0;
        foreach (string name in MemoryTypesToPublicize)
        {
            var type = module.GetType(name) ?? throw new InvalidOperationException($"MonoMod type {name} not found");
            if (type.IsPublic)
                continue;
            type.IsPublic = true;
            changed++;
        }
        return changed;
    }

    private sealed class ArenaModule
    {
        private readonly ModuleDefinition module;
        private readonly AssemblyDefinition arena;

        public ArenaModule(ModuleDefinition module, AssemblyDefinition arena)
        {
            this.module = module;
            this.arena = arena;
        }

        public MethodReference Method(string type, string name, int parameterCount)
        {
            var definition = arena.MainModule.GetType(type) ?? throw new InvalidOperationException($"{type} missing from {ArenaAssemblyName}");
            var method = definition.Methods.First(candidate => candidate.Name == name && candidate.Parameters.Count == parameterCount);
            return module.ImportReference(method);
        }
    }

    private static int UseStaticJitShim(ModuleDefinition module, ArenaModule arena)
    {
        var runtime = module.GetType("MonoMod.Core.Platforms.Runtimes.CoreBaseRuntime");
        var method = runtime.Methods.First(candidate => candidate.Name == "GetClrJitPath");
        var instructions = method.Body.Instructions;
        bool done = instructions.Count == 3
            && instructions[0].OpCode == OpCodes.Ldstr && (string)instructions[0].Operand == "libport17jit.dylib"
            && instructions[1].Operand is MethodReference resolve && resolve.DeclaringType.FullName == Port17ArenaType
            && instructions[2].OpCode == OpCodes.Ret;
        if (done)
            return 0;

        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        instructions.Clear();
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldstr, "libport17jit.dylib");
        il.Emit(OpCodes.Call, arena.Method(Port17ArenaType, "Resolve", 1));
        il.Emit(OpCodes.Ret);
        return 1;
    }

    private static int UseBundleExHelper(ModuleDefinition module, ArenaModule arena)
    {
        var system = module.GetType("MonoMod.Core.Platforms.Systems.MacOSSystem");
        var method = system.Methods.First(candidate => candidate.Name == "CreateNativeExceptionHelper");
        var body = method.Body;
        var instructions = body.Instructions;
        // The stock body also carries a ldstr "exhelper_macos_arm64.dylib" (arch switch) inside a
        // try/catch, so only a call into our resolver counts as done.
        bool done = instructions.Any(instruction => instruction.Operand is MethodReference call
                && call.DeclaringType.FullName == Port17ArenaType && call.Name == "Resolve");
        if (done)
            return 0;

        var arch = system.Fields.First(candidate => candidate.Name == "arch");
        var helper = system.NestedTypes.First(candidate => candidate.Name == "JitMemcpyHelper");
        var createHelper = helper.Methods.First(candidate => candidate.Name == "CreateHelper" && candidate.Parameters.Count == 3);
        var getMessage = module.ImportReference(typeof(Exception).GetProperty("Message")!.GetMethod!);
        var concat = module.ImportReference(typeof(string)
            .GetMethod("Concat", new[] { typeof(string), typeof(string) })!);
        var writeLine = module.ImportReference(typeof(Console)
            .GetMethod("WriteLine", new[] { typeof(string) })!);
        var disabled = arena.Method(Port17ArenaType, "get_ExhelperDisabled", 0);
        var markDisabled = arena.Method(Port17ArenaType, "MarkExhelperDisabled", 0);
        var resolve = arena.Method(Port17ArenaType, "Resolve", 1);

        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        body.Variables.Add(new VariableDefinition(method.ReturnType)); // result
        body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(Exception)))); // e
        var il = body.GetILProcessor();
        // if (Port17Arena.ExhelperDisabled) return null;
        il.Emit(OpCodes.Call, disabled);
        var tryStart = il.Create(OpCodes.Ldarg_0);
        il.Append(il.Create(OpCodes.Brfalse, tryStart));
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        // try { result = CreateHelper(arch, Resolve(...), false); leave }
        // catch (Exception e) { MarkExhelperDisabled(); log; result = null; leave } result; ret
        // — the CLR rejects `ret` inside a try/catch region (InvalidProgramException).
        il.Append(tryStart);
        il.Emit(OpCodes.Ldfld, arch);
        il.Emit(OpCodes.Ldstr, "exhelper_macos_arm64.dylib");
        il.Emit(OpCodes.Call, resolve);
        il.Emit(OpCodes.Ldc_I4_0); // createHelper(..., deleteAfterLoad: false)
        il.Emit(OpCodes.Call, createHelper);
        il.Emit(OpCodes.Stloc_0);
        var leaveTry = il.Create(OpCodes.Leave, tryStart); // target fixed below
        il.Append(leaveTry);
        var handlerStart = il.Create(OpCodes.Stloc_1);
        il.Append(handlerStart);
        il.Emit(OpCodes.Call, markDisabled);
        il.Emit(OpCodes.Ldstr, "[port17] monomod exhelper unavailable, continuing without native exception helper: ");
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Callvirt, getMessage);
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Call, writeLine);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc_0);
        var leaveHandler = il.Create(OpCodes.Leave, tryStart);
        il.Append(leaveHandler);
        var loadResult = il.Create(OpCodes.Ldloc_0);
        il.Append(loadResult);
        il.Emit(OpCodes.Ret);
        leaveTry.Operand = loadResult;
        leaveHandler.Operand = loadResult;
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = loadResult,
        });
        return 1;
    }

    private static int UseArenaAllocator(ModuleDefinition module, ArenaModule arena)
    {
        var system = module.GetType("MonoMod.Core.Platforms.Systems.MacOSSystem");
        var ctor = system.Methods.First(candidate => candidate.IsConstructor && !candidate.IsStatic);
        var backing = system.Fields.First(candidate => candidate.Name == "<MemoryAllocator>k__BackingField");
        if (ctor.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Newobj
                && instruction.Operand is MethodReference ctorCall && ctorCall.DeclaringType.FullName == Port17AllocatorType))
            return 0;
        var store = ctor.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Stfld
            && instruction.Operand is FieldReference field && field.Name == backing.Name);
        var il = ctor.Body.GetILProcessor();
        il.InsertBefore(store, Instruction.Create(OpCodes.Pop)); // drop the stock QueryingPagedMemoryAllocator
        il.InsertBefore(store, Instruction.Create(OpCodes.Newobj, arena.Method(Port17AllocatorType, ".ctor", 0)));
        return 1;
    }

    private static int MakePatchDataAliasAware(ModuleDefinition module, ArenaModule arena)
    {
        var system = module.GetType("MonoMod.Core.Platforms.Systems.MacOSSystem");
        var method = system.Methods.First(candidate => candidate.Name == "PatchData" && candidate.Parameters.Count == 4);
        if (method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference call
                && call.DeclaringType.FullName == Port17ArenaType && call.Name == "TryPatchViaAlias"))
            return 0;
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2)); // nint patchTarget
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_3)); // ReadOnlySpan<byte> data
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_S, method.Parameters[3])); // Span<byte> backup
        il.InsertBefore(first, Instruction.Create(OpCodes.Call, arena.Method(Port17ArenaType, "TryPatchViaAlias", 3)));
        il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
        return 1;
    }

    private static int SwapJitVtableInsteadOfPatching(ModuleDefinition module, ArenaModule arena)
    {
        var runtime = module.GetType("MonoMod.Core.Platforms.Runtimes.Core21Runtime");
        var method = runtime.Methods.First(candidate => candidate.Name == "InstallManagedJitHook");
        var instructions = method.Body.Instructions;
        if (instructions.Any(instruction => instruction.Operand is MethodReference call
                && call.DeclaringType.FullName == Port17ArenaType && call.Name == "SwapCompilerVtable"))
            return 0;
        if (method.Body.ExceptionHandlers.Count != 0)
            throw new InvalidOperationException("InstallManagedJitHook grew exception handlers; re-check the rewrite");

        // Short-form ldloc/stloc opcodes carry no VariableDefinition operand in Cecil 0.11.
        VariableDefinition VariableAt(int index)
        {
            var instruction = instructions[index];
            if (instruction.Operand is VariableDefinition variable)
                return variable;
            int local = instruction.OpCode.Code switch
            {
                Code.Stloc_0 or Code.Ldloc_0 => 0,
                Code.Stloc_1 or Code.Ldloc_1 => 1,
                Code.Stloc_2 or Code.Ldloc_2 => 2,
                Code.Stloc_3 or Code.Ldloc_3 => 3,
                _ => throw new InvalidOperationException($"expected a stloc after the call, found {instruction.OpCode}"),
            };
            return method.Body.Variables[local];
        }

        int IndexAfter(string callee)
        {
            int index = instructions.ToList().FindIndex(instruction => instruction.Operand is MethodReference call
                && call.Name == callee);
            if (index < 0)
                throw new InvalidOperationException($"{callee} not found in InstallManagedJitHook");
            return index + 1;
        }
        var vtableEntry = VariableAt(IndexAfter("GetVTableEntry"));
        var value = VariableAt(IndexAfter("EHNativeToManaged"));
        int keep = IndexAfter("InvokeCompileMethodToPrepare");

        var kept = instructions.Take(keep).ToList();
        instructions.Clear();
        var il = method.Body.GetILProcessor();
        foreach (var instruction in kept)
            il.Append(instruction);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, vtableEntry);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Call, arena.Method(Port17ArenaType, "SwapCompilerVtable", 3));
        il.Emit(OpCodes.Ret);
        return 1;
    }

    // MonoMod's Core110Runtime walks MethodDesc to obtain the body. MethodDesc::GetNativeCode()
    // returns only the default native-code slot; .NET 11's tiered/versionable methods can keep
    // the active body in CodeVersionManager instead. Keep the runtime's identifiable-method and
    // compile prologue, then ask the BCL's GetNativeCodeInternal (GetNativeCodeAnyVersion) path.
    private static int UseRuntimeMethodHandleEntryPoint(ModuleDefinition module, ArenaModule arena)
    {
        var runtime = module.GetType("MonoMod.Core.Platforms.Runtimes.Core110Runtime");
        var method = runtime.Methods.First(candidate => candidate.Name == "GetMethodEntryPoint" && candidate.Parameters.Count == 2);
        var instructions = method.Body.Instructions;
        var helper = arena.Method(Port17ArenaType, "GetCore110MethodEntryPoint", 2);
        if (instructions.Any(instruction => instruction.Operand is MethodReference call
                && call.DeclaringType.FullName == Port17ArenaType && call.Name == "GetCore110MethodEntryPoint"))
            return 0;

        int compile = instructions.ToList().FindIndex(instruction =>
            instruction.Operand is MethodReference call && call.Name == "TryInvokeBclCompileMethod");
        if (compile < 0)
            throw new InvalidOperationException("TryInvokeBclCompileMethod not found in Core110Runtime.GetMethodEntryPoint");
        int assert = -1;
        for (int i = compile + 1; i < instructions.Count; i++)
        {
            if (instructions[i].Operand is MethodReference call &&
                call.Name == "Assert" && call.DeclaringType.FullName == "MonoMod.Utils.Helpers")
            {
                assert = i;
                break;
            }
        }
        if (assert < 0)
            throw new InvalidOperationException("Helpers.Assert after TryInvokeBclCompileMethod not found");

        var kept = instructions.Take(assert + 1).ToList();
        instructions.Clear();
        method.Body.ExceptionHandlers.Clear();
        var il = method.Body.GetILProcessor();
        foreach (var instruction in kept)
            il.Append(instruction);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, helper);
        il.Emit(OpCodes.Ret);
        return 1;
    }

    // The runtime's allocMem blockRW is a short-lived alias (unmapped once the JIT is done), so
    // MonoMod must never patch through it: every read of fromRw becomes a read of from (the
    // executable arena address), and ISystem.PatchData routes the write through our alias.
    private static int DropRuntimeRwAlias(ModuleDefinition module)
    {
        int changed = 0;
        var triple = module.GetType("MonoMod.Core.Platforms.PlatformTriple");
        foreach (var method in triple.Methods.Where(candidate => candidate.Name is "CreateSimpleDetour" or "CreateNativeDetour"))
        {
            var from = method.Parameters.First(parameter => parameter.Name == "from");
            var fromRw = method.Parameters.First(parameter => parameter.Name == "fromRw");
            foreach (var instruction in method.Body.Instructions)
            {
                if ((instruction.OpCode.Code is Code.Ldarg or Code.Ldarg_S) && ReferenceEquals(instruction.Operand, fromRw))
                {
                    instruction.Operand = from;
                    changed++;
                }
            }
        }
        return changed;
    }

    private static int ForcePlatformTriple(ModuleDefinition module)
    {
        var detection = module.GetType("MonoMod.Utils.PlatformDetection");
        var method = detection.Methods.First(candidate => candidate.Name == "DetectPlatformInfo");
        var body = method.Body;
        var instructions = body.Instructions;
        bool done = instructions.Count >= 4
            && instructions[0].OpCode == OpCodes.Ldc_I4_S && (sbyte)instructions[0].Operand == 5
            && instructions[1].OpCode == OpCodes.Ldc_I4_S && (sbyte)instructions[1].Operand == 5
            && instructions[2].OpCode == OpCodes.Newobj
            && ((MethodReference)instructions[2].Operand).Name == ".ctor"
            && instructions[3].OpCode == OpCodes.Ret;
        if (done)
            return 0;

        var originalConstructor = instructions.First(instruction => instruction.OpCode == OpCodes.Newobj
            && instruction.Operand is MethodReference constructor
            && constructor.DeclaringType.FullName == method.ReturnType.FullName);
        var originalReturn = instructions.Last(instruction => instruction.OpCode == OpCodes.Ret);

        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var il = body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4_S, (sbyte)5); // OSKind.OSX: the only macOS-family backend MonoMod implements
        il.Emit(OpCodes.Ldc_I4_S, (sbyte)5); // ArchitectureKind.Arm64
        il.Append(originalConstructor);
        il.Append(originalReturn);
        return 1;
    }

    // MonoMod's Core110Runtime was written against a .NET 11 preview whose CEEJitInfo vtable had
    // 184 slots; the v11.0.0 GA binary (runtime-out-hybrid/libcoreclr.dylib, __ZTV10CEEJitInfo)
    // has 191 — allocMem is still 167 and getJitFlags still 183, but slots 184..190 (dtor x2,
    // ResetForJitRetry, SetDebugInfo, GetPatchpointInfo, JitAgain, WriteCode) would fall past
    // MonoMod's 184-entry proxy vtable and jump into garbage. Pin the measured values.
    private const int ExpectedAllocMemIndex = 167;
    private const int ExpectedGetJitFlagsIndex = 183;
    private const int MeasuredVtableCount = 191;

    private static int PatchCore110VtableCount(ModuleDefinition module)
    {
        var runtime = module.GetType("MonoMod.Core.Platforms.Runtimes.Core110Runtime");
        int Constant(string name)
        {
            var getter = runtime.Methods.First(candidate => candidate.Name == name);
            var instructions = getter.Body.Instructions;
            if (instructions.Count != 2 || instructions[0].OpCode != OpCodes.Ldc_I4 || instructions[1].OpCode != OpCodes.Ret)
                throw new InvalidOperationException($"unexpected {name} body");
            return (int)instructions[0].Operand;
        }

        int allocMem = Constant("get_VtableIndexICorJitInfoAllocMem");
        int getJitFlags = Constant("get_VtableIndexICorJitInfoGetJitFlags");
        if (allocMem != ExpectedAllocMemIndex || getJitFlags != ExpectedGetJitFlagsIndex)
            throw new InvalidOperationException($"Core110Runtime vtable indices changed (allocMem={allocMem}, getJitFlags={getJitFlags}); re-measure against libcoreclr");

        var countGetter = runtime.Methods.First(candidate => candidate.Name == "get_ICorJitInfoFullVtableCount");
        int before = (int)countGetter.Body.Instructions[0].Operand;
        if (before == MeasuredVtableCount)
            return 0;
        countGetter.Body.Instructions[0].Operand = MeasuredVtableCount;
        Console.WriteLine($"coreclr vtable: ICorJitInfoFullVtableCount {before} -> {MeasuredVtableCount} (allocMem={allocMem}, getJitFlags={getJitFlags})");
        return 1;
    }

    private static MethodReference ImportPortPlatform(ModuleDefinition module, DefaultAssemblyResolver resolver,
        Dictionary<string, MethodReference> cache, string methodName)
    {
        if (cache.TryGetValue(methodName, out MethodReference done))
            return done;
        var hooks = resolver.Resolve(new AssemblyNameReference("PortHooks", new Version(1, 0, 0, 0)));
        var platform = hooks.MainModule.GetType("PortHooks.PortPlatform");
        return cache[methodName] = module.ImportReference(platform.Methods.First(method => method.Name == methodName));
    }
}
