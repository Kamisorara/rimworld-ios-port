// Applies the same iOS Harmony compatibility rewrites as device setup, before the IPA is built.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class Program
{
    private static readonly Dictionary<string, string> HarmonyRewrites = new()
    {
        ["System.Reflection.FieldInfo System.Type::GetField(System.String,System.Reflection.BindingFlags)"] = "GetFieldLax",
    };

    private static readonly Dictionary<string, string> CallOpcodeFilters = new()
    {
        ["System.Void HarmonyLib.Emitter::Emit(System.Reflection.Emit.OpCode,System.Reflection.MethodInfo)"] = "LaxCallOpcode",
        ["System.Void HarmonyLib.Emitter::EmitCall(System.Reflection.Emit.OpCode,System.Reflection.MethodInfo)"] = "LaxCallOpcode",
        ["System.Void HarmonyLib.Emitter::DynEmit(System.Reflection.Emit.OpCode,System.Object)"] = "LaxCallOpcodeForOperand",
    };

    private const string EmitCodes =
        "System.Void HarmonyLib.MethodCreatorTools::EmitCodes(HarmonyLib.MethodCreator,HarmonyLib.Emitter,System.Collections.Generic.List`1<HarmonyLib.CodeInstruction>)";

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

        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: Port17PatchHarmony <0Harmony.dll> <output folder> <PortHooks.dll>");
            return 2;
        }

        string source = Path.GetFullPath(args[0]);
        string outputFolder = Path.GetFullPath(args[1]);
        string hooks = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(outputFolder);
        string output = Path.Combine(outputFolder, "0Harmony.dll");
        string reading = source;
        bool inPlace = string.Equals(source, output, StringComparison.OrdinalIgnoreCase);
        if (inPlace)
        {
            reading = output + ".source.p17tmp";
            File.Copy(source, reading, true);
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(outputFolder);
        resolver.AddSearchDirectory(Path.GetDirectoryName(hooks)!);
        var assembly = AssemblyDefinition.ReadAssembly(reading, new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadingMode = ReadingMode.Immediate,
        });

        using (assembly)
        {
            var imported = new Dictionary<string, MethodReference>();
            int changed = 0;
            foreach (var type in assembly.MainModule.GetTypes())
            {
                bool inHarmonyLib = type.FullName.StartsWith("HarmonyLib.", StringComparison.Ordinal);
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;

                    if (CallOpcodeFilters.TryGetValue(method.FullName, out string filterName))
                    {
                        var filter = ImportPortPlatform(assembly.MainModule, resolver, imported, filterName);
                        var first = method.Body.Instructions[0];
                        bool done = method.Body.Instructions.Count > 2 &&
                            method.Body.Instructions[2].Operand is MethodReference already &&
                            already.FullName == filter.FullName;
                        if (!done)
                        {
                            var il = method.Body.GetILProcessor();
                            il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
                            il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_2));
                            il.InsertBefore(first, Instruction.Create(OpCodes.Call, filter));
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

            changed += UseBundleExHelper(assembly.MainModule);
            changed += UseStaticJitShim(assembly.MainModule);
            changed += ForcePlatformTriple(assembly.MainModule);

            string temp = output + ".p17tmp";
            assembly.Write(temp);
            File.Move(temp, output, true);
            Console.WriteLine($"0Harmony.dll: {changed} rewrites -> {output}");
        }
        if (inPlace)
            File.Delete(reading);
        return 0;
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

    private static int UseStaticJitShim(ModuleDefinition module)
    {
        var runtime = module.GetType("MonoMod.Core.Platforms.Runtimes.CoreBaseRuntime");
        var method = runtime.Methods.First(candidate => candidate.Name == "GetClrJitPath");
        var body = method.Body;
        var instructions = body.Instructions;
        bool done = instructions.Count == 4
            && instructions[0].Operand is MethodReference start && start.Name == "get_BaseDirectory"
            && instructions[2].Operand is MethodReference combine && combine.Name == "Combine"
            && instructions[3].OpCode == OpCodes.Ret;
        if (done)
            return 0;

        var baseDirectory = module.ImportReference(typeof(AppContext)
            .GetProperty("BaseDirectory", BindingFlags.Static | BindingFlags.Public)!
            .GetMethod);
        var pathCombine = module.ImportReference(typeof(System.IO.Path)
            .GetMethod("Combine", new[] { typeof(string), typeof(string) }));

        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var il = body.GetILProcessor();
        il.Emit(OpCodes.Call, baseDirectory);
        il.Emit(OpCodes.Ldstr, "libport17jit.dylib");
        il.Emit(OpCodes.Call, pathCombine);
        il.Emit(OpCodes.Ret);
        return 1;
    }

    private static int UseBundleExHelper(ModuleDefinition module)
    {
        var system = module.GetType("MonoMod.Core.Platforms.Systems.MacOSSystem");
        var method = system.Methods.First(candidate => candidate.Name == "CreateNativeExceptionHelper");
        var body = method.Body;
        var instructions = body.Instructions;
        bool done = instructions.Count == 8
            && instructions[1].Operand is FieldReference field && field.Name == "arch"
            && instructions[6].Operand is MethodReference call && call.Name == "CreateHelper";
        if (done)
            return 0;

        var arch = system.Fields.First(candidate => candidate.Name == "arch");
        var helper = system.NestedTypes.First(candidate => candidate.Name == "JitMemcpyHelper");
        var createHelper = helper.Methods.First(candidate => candidate.Name == "CreateHelper" && candidate.Parameters.Count == 3);
        var baseDirectory = module.ImportReference(typeof(AppContext)
            .GetProperty("BaseDirectory", BindingFlags.Static | BindingFlags.Public)!
            .GetMethod);
        var combine = module.ImportReference(typeof(System.IO.Path)
            .GetMethod("Combine", new[] { typeof(string), typeof(string) }));

        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var il = body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, arch);
        il.Emit(OpCodes.Call, baseDirectory);
        il.Emit(OpCodes.Ldstr, "exhelper_macos_arm64.dylib");
        il.Emit(OpCodes.Call, combine);
        il.Emit(OpCodes.Ldc_I4_0); // createHelper(..., deleteAfterLoad: false)
        il.Emit(OpCodes.Call, createHelper);
        il.Emit(OpCodes.Ret);
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
