// Applies the same iOS Harmony compatibility rewrites as device setup, before the IPA is built.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
        bool done = instructions.Count == 3
            && instructions[0].OpCode == OpCodes.Ldstr && (string)instructions[0].Operand == "libport17jit.dylib"
            && instructions[1].Operand is MethodReference resolve && resolve.Name == "Resolve"
            && instructions[2].OpCode == OpCodes.Ret;
        if (done)
            return 0;

        int injected = InjectHostFiles(module);
        var resolveShim = module.GetType("MonoMod.Core.Platforms.Port17HostFiles").Methods
            .First(candidate => candidate.Name == "Resolve");

        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var il = body.GetILProcessor();
        il.Emit(OpCodes.Ldstr, "libport17jit.dylib");
        il.Emit(OpCodes.Call, resolveShim);
        il.Emit(OpCodes.Ret);
        return 1 + injected;
    }

    private static int UseBundleExHelper(ModuleDefinition module)
    {
        var system = module.GetType("MonoMod.Core.Platforms.Systems.MacOSSystem");
        var method = system.Methods.First(candidate => candidate.Name == "CreateNativeExceptionHelper");
        var body = method.Body;
        var instructions = body.Instructions;
        // The stock body also carries a ldstr "exhelper_macos_arm64.dylib" (arch switch) inside a
        // try/catch, so only a call into our resolver counts as done.
        bool done = instructions.Any(instruction => instruction.Operand is MethodReference call
                && call.Name == "Resolve")
            && instructions.Any(instruction => instruction.Operand is MethodReference call
                && call.Name == "CreateHelper");
        if (done)
            return 0;

        var arch = system.Fields.First(candidate => candidate.Name == "arch");
        var helper = system.NestedTypes.First(candidate => candidate.Name == "JitMemcpyHelper");
        var createHelper = helper.Methods.First(candidate => candidate.Name == "CreateHelper" && candidate.Parameters.Count == 3);
        int injected = InjectHostFiles(module);
        var resolveShim = module.GetType("MonoMod.Core.Platforms.Port17HostFiles").Methods
            .First(candidate => candidate.Name == "Resolve");
        var getMessage = module.ImportReference(typeof(Exception).GetProperty("Message")!.GetMethod);
        var concat = module.ImportReference(typeof(string)
            .GetMethod("Concat", new[] { typeof(string), typeof(string) }));
        var writeLine = module.ImportReference(typeof(Console)
            .GetMethod("WriteLine", new[] { typeof(string) }));

        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(Exception))));
        var il = body.GetILProcessor();
        // try { return JitMemcpyHelper.CreateHelper(arch, Resolve("exhelper_macos_arm64.dylib"), false); }
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, arch);
        il.Emit(OpCodes.Ldstr, "exhelper_macos_arm64.dylib");
        il.Emit(OpCodes.Call, resolveShim);
        il.Emit(OpCodes.Ldc_I4_0); // createHelper(..., deleteAfterLoad: false)
        il.Emit(OpCodes.Call, createHelper);
        il.Emit(OpCodes.Ret);
        var tryStart = instructions[0];
        var handlerStart = il.Create(OpCodes.Stloc_0);
        il.Append(handlerStart);
        // catch (Exception e) { Console.WriteLine("[port17] monomod exhelper unavailable: " + e.Message); return null; }
        il.Emit(OpCodes.Ldstr, "[port17] monomod exhelper unavailable, continuing without native exception helper: ");
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Callvirt, getMessage);
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Call, writeLine);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = instructions[instructions.Count - 1].Next ?? instructions[instructions.Count - 1],
        });
        return 1 + injected;
    }

    // iOS: AppContext.BaseDirectory is the game folder (engine.c sets APP_CONTEXT_BASE_DIRECTORY),
    // while the host ships MonoMod's native files in the .app bundle next to 0Harmony.dll. Resolve
    // against the executing assembly first, then the game folder and the process folder; probe-load
    // the result so dyld's refusal ends up in the log instead of MonoMod's generic "Could not open".
    private static int InjectHostFiles(ModuleDefinition module)
    {
        const string typeName = "Port17HostFiles";
        var existing = module.GetType("MonoMod.Core.Platforms." + typeName);
        if (existing != null)
            return 0;

        var type = new TypeDefinition("MonoMod.Core.Platforms", typeName,
            Mono.Cecil.TypeAttributes.NotPublic | Mono.Cecil.TypeAttributes.Abstract
            | Mono.Cecil.TypeAttributes.Sealed | Mono.Cecil.TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition("Resolve",
            Mono.Cecil.MethodAttributes.Assembly | Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.HideBySig,
            module.TypeSystem.String);
        method.Parameters.Add(new ParameterDefinition("fileName", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.String));
        type.Methods.Add(method);

        var body = method.Body;
        body.InitLocals = true;
        for (int i = 0; i < 4; i++)
            body.Variables.Add(new VariableDefinition(module.TypeSystem.String)); // dir, path, game, exe
        body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(Exception))));

        MethodReference M(MethodInfo info) => module.ImportReference(info);
        var getExecuting = M(typeof(Assembly).GetMethod(nameof(Assembly.GetExecutingAssembly), Type.EmptyTypes)!);
        var getLocation = M(typeof(Assembly).GetProperty(nameof(Assembly.Location))!.GetMethod!);
        var getDirName = M(typeof(Path).GetMethod(nameof(Path.GetDirectoryName), new[] { typeof(string) })!);
        var combine = M(typeof(Path).GetMethod(nameof(Path.Combine), new[] { typeof(string), typeof(string) })!);
        var fileExists = M(typeof(File).GetMethod(nameof(File.Exists), new[] { typeof(string) })!);
        var isNullOrEmpty = M(typeof(string).GetMethod(nameof(string.IsNullOrEmpty), new[] { typeof(string) })!);
        var getBaseDirectory = M(typeof(AppContext).GetProperty(nameof(AppContext.BaseDirectory))!.GetMethod!);
        var getProcessPath = M(typeof(Environment).GetProperty(nameof(Environment.ProcessPath))!.GetMethod!);
        var writeLine = M(typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(string) })!);
        var concat2 = M(typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!);
        var concat3 = M(typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string), typeof(string) })!);
        var getMessage = M(typeof(Exception).GetProperty(nameof(Exception.Message))!.GetMethod!);
        var nativeLoad = M(typeof(NativeLibrary).GetMethod(nameof(NativeLibrary.Load), new[] { typeof(string) })!);
        var nativeFree = M(typeof(NativeLibrary).GetMethod(nameof(NativeLibrary.Free), new[] { typeof(IntPtr) })!);

        var il = body.GetILProcessor();
        // Branch targets are wired after the full body is laid out (Cecil rejects null targets).
        var placeholder = il.Create(OpCodes.Nop);
        Instruction Branch(OpCode op) => il.Create(op, placeholder);
        // dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
        il.Emit(OpCodes.Call, getExecuting);
        il.Emit(OpCodes.Callvirt, getLocation);
        il.Emit(OpCodes.Call, getDirName);
        il.Emit(OpCodes.Stloc_0);
        // path = IsNullOrEmpty(dir) ? null : Path.Combine(dir, fileName)
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Call, isNullOrEmpty);
        var toGame = Branch(OpCodes.Brtrue);
        il.Append(toGame);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, combine);
        il.Emit(OpCodes.Stloc_1);
        var toCheck = Branch(OpCodes.Br);
        il.Append(toCheck);
        var game = il.Create(OpCodes.Ldnull);
        il.Append(game);
        toGame.Operand = game;
        il.Emit(OpCodes.Stloc_1);
        // if (IsNullOrEmpty(path) || !File.Exists(path)) { game = Combine(BaseDirectory, fileName); if (File.Exists(game)) path = game; }
        var check = il.Create(OpCodes.Ldloc_1);
        il.Append(check);
        toCheck.Operand = check;
        il.Emit(OpCodes.Call, isNullOrEmpty);
        var toGameFallback = Branch(OpCodes.Brtrue);
        il.Append(toGameFallback);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Call, fileExists);
        var toHave = Branch(OpCodes.Brtrue);
        il.Append(toHave);
        var gameFallback = il.Create(OpCodes.Call, getBaseDirectory);
        il.Append(gameFallback);
        toGameFallback.Operand = gameFallback;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, combine);
        il.Emit(OpCodes.Stloc_2);
        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Call, fileExists);
        var toExe = Branch(OpCodes.Brfalse);
        il.Append(toExe);
        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Stloc_1);
        var toHaveFromGame = Branch(OpCodes.Br);
        il.Append(toHaveFromGame);
        // exe = Environment.ProcessPath; dir = Path.GetDirectoryName(exe); if (!IsNullOrEmpty(dir)) path = Combine(dir, fileName)
        var exeFallback = il.Create(OpCodes.Call, getProcessPath);
        il.Append(exeFallback);
        toExe.Operand = exeFallback;
        il.Emit(OpCodes.Stloc_3);
        il.Emit(OpCodes.Ldloc_3);
        il.Emit(OpCodes.Call, isNullOrEmpty);
        var toFinal = Branch(OpCodes.Brtrue);
        il.Append(toFinal);
        il.Emit(OpCodes.Ldloc_3);
        il.Emit(OpCodes.Call, getDirName);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Call, isNullOrEmpty);
        var toFinalFromDir = Branch(OpCodes.Brtrue);
        il.Append(toFinalFromDir);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, combine);
        il.Emit(OpCodes.Stloc_1);
        var toHaveFromExe = Branch(OpCodes.Br);
        il.Append(toHaveFromExe);
        // if (IsNullOrEmpty(path)) path = Combine(BaseDirectory, fileName)
        var final = il.Create(OpCodes.Ldloc_1);
        il.Append(final);
        toFinal.Operand = final;
        toFinalFromDir.Operand = final;
        il.Emit(OpCodes.Call, isNullOrEmpty);
        var toHaveDefault = Branch(OpCodes.Brfalse);
        il.Append(toHaveDefault);
        il.Emit(OpCodes.Call, getBaseDirectory);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, combine);
        il.Emit(OpCodes.Stloc_1);
        // Console.WriteLine("[port17] monomod shim " + fileName + " -> " + path);
        var have = il.Create(OpCodes.Ldstr, "[port17] monomod shim ");
        il.Append(have);
        toHave.Operand = have;
        toHaveFromGame.Operand = have;
        toHaveFromExe.Operand = have;
        toHaveDefault.Operand = have;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, concat2);
        il.Emit(OpCodes.Ldstr, " -> ");
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Call, concat3);
        il.Emit(OpCodes.Call, writeLine);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Call, fileExists);
        var skipMissing = Branch(OpCodes.Brtrue);
        il.Append(skipMissing);
        il.Emit(OpCodes.Ldstr, "[port17] monomod shim missing: ");
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Call, concat2);
        il.Emit(OpCodes.Call, writeLine);
        // try { NativeLibrary.Free(NativeLibrary.Load(path)); } catch (Exception e) { Console.WriteLine("[port17] monomod shim load failed: " + e.Message); }
        var tryStart = il.Create(OpCodes.Ldloc_1);
        il.Append(tryStart);
        skipMissing.Operand = tryStart;
        il.Emit(OpCodes.Call, nativeLoad);
        il.Emit(OpCodes.Call, nativeFree);
        var leaveTry = Branch(OpCodes.Leave);
        il.Append(leaveTry);
        var handlerStart = il.Create(OpCodes.Stloc_S, body.Variables[4]);
        il.Append(handlerStart);
        il.Emit(OpCodes.Ldstr, "[port17] monomod shim load failed: ");
        il.Emit(OpCodes.Ldloc_S, body.Variables[4]);
        il.Emit(OpCodes.Callvirt, getMessage);
        il.Emit(OpCodes.Call, concat2);
        il.Emit(OpCodes.Call, writeLine);
        var leaveHandler = Branch(OpCodes.Leave);
        il.Append(leaveHandler);
        var ret = il.Create(OpCodes.Ldloc_1);
        il.Append(ret);
        leaveTry.Operand = ret;
        leaveHandler.Operand = ret;
        il.Emit(OpCodes.Ret);
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = module.ImportReference(typeof(Exception)),
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = ret,
        });
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
