// Applies the same iOS Harmony compatibility rewrites as device setup, before the IPA is built.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            string temp = output + ".p17tmp";
            assembly.Write(temp);
            File.Move(temp, output, true);
            Console.WriteLine($"0Harmony.dll: {changed} rewrites -> {output}");
        }
        if (inPlace)
            File.Delete(reading);
        return 0;
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
