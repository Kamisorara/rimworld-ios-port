using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodBody = Mono.Cecil.Cil.MethodBody;

// The iOS runtime pack ships System.IO.FileSystem.Watcher as a stub: every member but Dispose throws
// PlatformNotSupportedException. Mods take the class as it is on a desktop. CameraPlus, for one, makes a watcher
// on its folder for the hot reload of its custom markers (Source/Assets.cs, LoadShaders); on the phone the
// exception leaves the rest of that method undone and is logged as a critical error at every start (0.4.42). A
// Harmony prefix does not do here: it is a native detour, which the inlined copy of a one-line throw in a mod's
// own JIT code does not go through. The stub is pure IL, so its bodies are rewritten instead, once, before the
// IPA is built, into what a watcher that never sees a change does. Nothing else in the assembly is touched and
// its identity (name, version, public key) stays, so mods bind to it as before; CoreCLR does not check the
// strong-name signature, which the rewrite leaves as it was.
//
//   unstub <stub dll> <output dll>   rewrite every body that only throws; 1 if none was or one is left
//   smoke <output dll>               load the output apart from the runtime's own copy and drive it the way
//                                    CameraPlus does: what the JIT refuses shows here, not on the phone
//
// scripts/build-portlayers.py runs both; the output is prebuilt/System.IO.FileSystem.Watcher.dll.
internal static class Program
{
    private const string Io = "System.IO";
    private const string NotSupported = "System.PlatformNotSupportedException";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "unstub")
            {
                return Unstub(args[1], args[2]);
            }
            if (args.Length == 2 && args[0] == "smoke")
            {
                return Smoke(args[1]);
            }
        }
        catch (Exception e)
        {
            // What a member run by reflection threw.
            Console.WriteLine("unstub-watcher: " + (e is TargetInvocationException && e.InnerException != null ? e.InnerException : e));
            return 1;
        }
        Console.WriteLine("usage: unstub <stub dll> <output dll> | smoke <output dll>");
        return 2;
    }

    private static int Unstub(string input, string output)
    {
        using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(input))
        {
            ModuleDefinition module = assembly.MainModule;
            int rewritten = 0;
            foreach (TypeDefinition type in module.GetTypes())
            {
                if (type.Namespace != Io)
                {
                    continue;
                }
                foreach (MethodDefinition method in type.Methods)
                {
                    if (ThrowsOnly(method))
                    {
                        Rewrite(module, type, method);
                        rewritten++;
                    }
                }
            }
            List<string> left = module.GetTypes().Where(t => t.Namespace == Io).SelectMany(t => t.Methods)
                .Where(ThrowsOnly).Select(m => m.FullName).ToList();
            if (rewritten == 0 || left.Count > 0)
            {
                Console.WriteLine("unstub-watcher: " + rewritten + " bodies rewritten, " + left.Count
                    + " still only throw: " + string.Join(", ", left));
                return 1;
            }
            string temporary = output + ".tmp";
            assembly.Write(temporary);
            File.Move(temporary, output, true);
            Console.WriteLine(Path.GetFileName(output) + ": " + rewritten + " bodies rewritten");
            Console.WriteLine("  from " + Header(input));
            Console.WriteLine("  to   " + Header(output));
            return 0;
        }
    }

    // A body of the shape `throw new PlatformNotSupportedException(SR.FileSystemWatcher_PlatformNotSupported);`, with
    // what a constructor puts before it: the base class's constructor and its arguments.
    private static bool ThrowsOnly(MethodDefinition method)
    {
        if (!method.HasBody)
        {
            return false;
        }
        MethodBody body = method.Body;
        if (body.ExceptionHandlers.Count != 0 || body.Variables.Count != 0 || body.Instructions.Count == 0)
        {
            return false;
        }
        if (body.Instructions[body.Instructions.Count - 1].OpCode != OpCodes.Throw)
        {
            return false;
        }
        int news = 0;
        foreach (Instruction instruction in body.Instructions)
        {
            OpCode code = instruction.OpCode;
            if (code == OpCodes.Nop || code == OpCodes.Ldstr || code == OpCodes.Ldnull || code == OpCodes.Ldarg
                || code == OpCodes.Ldarg_0 || code == OpCodes.Ldarg_1 || code == OpCodes.Ldarg_2 || code == OpCodes.Ldarg_3
                || code == OpCodes.Ldc_I4 || code == OpCodes.Ldc_I4_S || code.Code >= Code.Ldc_I4_M1 && code.Code <= Code.Ldc_I4_8
                || code == OpCodes.Throw)
            {
                continue;
            }
            if (code == OpCodes.Call || code == OpCodes.Callvirt)
            {
                // The message, from the assembly's own resource helper; the base class's constructor.
                if (!(instruction.Operand is MethodReference called) || (called.DeclaringType.FullName != "System.SR" && called.Name != ".ctor"))
                {
                    return false;
                }
                continue;
            }
            if (code == OpCodes.Newobj)
            {
                if (!(instruction.Operand is MethodReference constructor)
                    || constructor.DeclaringType.FullName != NotSupported)
                {
                    return false;
                }
                news++;
                continue;
            }
            return false;
        }
        return news == 1;
    }

    // A type of the runtime's core library itself: the facade the stub refers to (System.Runtime) hands out
    // Path but not Thread.
    private static TypeReference Core(ModuleDefinition module, string ns, string name)
    {
        AssemblyNameReference core = module.AssemblyReferences.FirstOrDefault(r => r.Name == "System.Private.CoreLib");
        if (core == null)
        {
            core = new AssemblyNameReference("System.Private.CoreLib", module.AssemblyReferences.First(r => r.Name == "System.Runtime").Version)
            {
                PublicKeyToken = new byte[] { 0x7c, 0xec, 0x85, 0xd7, 0xbe, 0xa7, 0x79, 0x8e },
            };
            module.AssemblyReferences.Add(core);
        }
        return new TypeReference(ns, name, module, core);
    }

    private static void Rewrite(ModuleDefinition module, TypeDefinition type, MethodDefinition method)
    {
        method.Body = new MethodBody(method);
        ILProcessor il = method.Body.GetILProcessor();
        switch (type.Name)
        {
            case "FileSystemWatcher":
                Watcher(module, type, method, il);
                break;
            case "FileSystemEventArgs":
                ChangedArgs(module, type, method, il);
                break;
            case "RenamedEventArgs":
                RenamedArgs(module, type, method, il);
                break;
            case "ErrorEventArgs":
                ErrorArgs(module, type, method, il);
                break;
            case "WaitForChangedResult":
                Property(type, method, il);
                break;
            case "InternalBufferOverflowException":
                ExceptionConstructor(module, type, method, il);
                break;
            default:
                throw new Exception(type.FullName + "." + method.Name + " only throws, and no rule says what it does instead");
        }
        // What a rule left on the stack is the return value, if the method has one.
        il.Emit(OpCodes.Ret);
    }

    // What a watcher that never sees a change does.
    private static void Watcher(ModuleDefinition module, TypeDefinition type, MethodDefinition method, ILProcessor il)
    {
        TypeSystem system = module.TypeSystem;
        if (method.IsConstructor)
        {
            // As on a desktop: Component's constructor, Filter "*.*", InternalBufferSize 8192, NotifyFilter
            // LastWrite | FileName | DirectoryName, an empty Filters; then the path and the filter given.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, Constructor(system, type.BaseType));
            Set(il, Field(type, "_filter", system.String), p => p.Emit(OpCodes.Ldstr, "*.*"));
            Set(il, Field(type, "_internalBufferSize", system.Int32), p => p.Emit(OpCodes.Ldc_I4, 8192));
            Set(il, Field(type, "_notifyFilter", module.GetType(Io + ".NotifyFilters")), p => p.Emit(OpCodes.Ldc_I4, 0x13));
            FieldDefinition filters = Field(type, "_filters", type.Properties.First(p => p.Name == "Filters").PropertyType);
            Set(il, filters, p => p.Emit(OpCodes.Newobj, new MethodReference(".ctor", system.Void, filters.FieldType) { HasThis = true }));
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                Store(method, il, Field(type, i == 0 ? "_path" : "_filter", system.String), i);
            }
        }
        else if (method.Name == "WaitForChanged")
        {
            // The wait lasts the time given, then says: nothing changed. Without a time, as on a desktop with
            // nothing changing, it does not end.
            TypeDefinition result = module.GetType(Io + ".WaitForChangedResult");
            VariableDefinition local = new VariableDefinition(result);
            method.Body.Variables.Add(local);
            method.Body.InitLocals = true;
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Initobj, result);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Call, new MethodReference("set_TimedOut", system.Void, result) { HasThis = true, Parameters = { new ParameterDefinition(system.Boolean) } });
            MethodReference sleep = new MethodReference("Sleep", system.Void,
                Core(module, "System.Threading", "Thread")) { Parameters = { new ParameterDefinition(system.Int32) } };
            if (method.Parameters.Count == 2)
            {
                // A struct is handed to its own method by address.
                il.Emit(method.Parameters[1].ParameterType.FullName == "System.TimeSpan" ? OpCodes.Ldarga : OpCodes.Ldarg, method.Parameters[1]);
                if (method.Parameters[1].ParameterType.FullName == "System.TimeSpan")
                {
                    il.Emit(OpCodes.Call, new MethodReference("get_TotalMilliseconds", system.Double, method.Parameters[1].ParameterType) { HasThis = true });
                    il.Emit(OpCodes.Conv_I4);
                }
                il.Emit(OpCodes.Call, sleep);
            }
            else
            {
                il.Emit(OpCodes.Ldc_I4_M1);
                il.Emit(OpCodes.Call, sleep);
            }
            il.Emit(OpCodes.Ldloc, local);
        }
        else if (type.Properties.Any(p => p.GetMethod == method || p.SetMethod == method))
        {
            // What was set is given back.
            Property(type, method, il);
        }
        else
        {
            // The events (no handler is kept: nothing raises them), BeginInit, EndInit, OnChanged and its kind.
            Nothing(type, method);
        }
    }

    // (WatcherChangeTypes changeType, string directory, string name)
    private static void ChangedArgs(ModuleDefinition module, TypeDefinition type, MethodDefinition method, ILProcessor il)
    {
        TypeSystem system = module.TypeSystem;
        if (method.IsConstructor)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, Constructor(system, type.BaseType));
            Store(method, il, Field(type, "_changeType", method.Parameters[0].ParameterType, true), 0);
            Store(method, il, Field(type, "_directory", method.Parameters[1].ParameterType, true), 1);
            Store(method, il, Field(type, "_name", method.Parameters[2].ParameterType, true), 2);
        }
        else if (method.Name == "get_FullPath")
        {
            Combine(module, il, Field(type, "_directory", system.String, true), Field(type, "_name", system.String, true));
        }
        else
        {
            Property(type, method, il);
        }
    }

    // FileSystemEventArgs(changeType, directory, name), then the old name.
    private static void RenamedArgs(ModuleDefinition module, TypeDefinition type, MethodDefinition method, ILProcessor il)
    {
        TypeSystem system = module.TypeSystem;
        if (method.IsConstructor)
        {
            il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < 3; i++)
            {
                il.Emit(OpCodes.Ldarg, method.Parameters[i]);
            }
            il.Emit(OpCodes.Call, Constructor(system, type.BaseType, method.Parameters[0].ParameterType, method.Parameters[1].ParameterType, method.Parameters[2].ParameterType));
            Store(method, il, Field(type, "_oldName", method.Parameters[3].ParameterType), 3);
        }
        else if (method.Name == "get_OldFullPath")
        {
            Combine(module, il, Field(module.GetType(Io + ".FileSystemEventArgs"), "_directory", system.String, true), Field(type, "_oldName", system.String));
        }
        else
        {
            Property(type, method, il);
        }
    }

    private static void ErrorArgs(ModuleDefinition module, TypeDefinition type, MethodDefinition method, ILProcessor il)
    {
        if (method.IsConstructor)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, Constructor(module.TypeSystem, type.BaseType));
            Store(method, il, Field(type, "_exception", method.Parameters[0].ParameterType), 0);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, Field(type, "_exception", method.ReturnType));
        }
    }

    // The exception keeps its constructors: the base class has each with the same parameters.
    private static void ExceptionConstructor(ModuleDefinition module, TypeDefinition type, MethodDefinition method, ILProcessor il)
    {
        il.Emit(OpCodes.Ldarg_0);
        MethodReference baseConstructor = Constructor(module.TypeSystem, type.BaseType);
        foreach (ParameterDefinition parameter in method.Parameters)
        {
            il.Emit(OpCodes.Ldarg, parameter);
            baseConstructor.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        }
        il.Emit(OpCodes.Call, baseConstructor);
    }

    // A getter or a setter of one of the type's properties: a field of its own keeps the value.
    private static void Property(TypeDefinition type, MethodDefinition method, ILProcessor il)
    {
        PropertyDefinition property = type.Properties.FirstOrDefault(p => p.GetMethod == method || p.SetMethod == method)
            ?? throw new Exception(type.FullName + "." + method.Name + " only throws, and no rule says what it does instead");
        FieldDefinition field = Field(type, "_" + Lower(property.Name), property.PropertyType);
        if (method == property.GetMethod)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
        }
        else
        {
            Store(method, il, field, 0);
        }
    }

    // A member that gives nothing back and has nothing to do.
    private static void Nothing(TypeDefinition type, MethodDefinition method)
    {
        if (method.ReturnType.FullName != "System.Void")
        {
            throw new Exception(type.FullName + "." + method.Name + " only throws and gives back a "
                + method.ReturnType.FullName + ": no rule says what it returns");
        }
    }

    // ---- IL pieces ----

    // A private field of the type, made on first use (`assembly` when another class of the stub reads it).
    private static FieldDefinition Field(TypeDefinition type, string name, TypeReference fieldType, bool assembly = false)
    {
        FieldDefinition field = type.Fields.FirstOrDefault(f => f.Name == name);
        if (field == null)
        {
            field = new FieldDefinition(name, assembly ? FieldAttributes.Assembly : FieldAttributes.Private, fieldType);
            type.Fields.Add(field);
        }
        return field;
    }

    // this, the parameter given: the field holds the parameter.
    private static void Store(MethodDefinition method, ILProcessor il, FieldDefinition field, int parameter)
    {
        Set(il, field, p => p.Emit(OpCodes.Ldarg, method.Parameters[parameter]));
    }

    // this, the value: the field holds the value.
    private static void Set(ILProcessor il, FieldDefinition field, Action<ILProcessor> value)
    {
        il.Emit(OpCodes.Ldarg_0);
        value(il);
        il.Emit(OpCodes.Stfld, field);
    }

    // this.folder, this.name: System.IO.Path.Combine(folder, name).
    private static void Combine(ModuleDefinition module, ILProcessor il, FieldDefinition folder, FieldDefinition name)
    {
        MethodReference combine = new MethodReference("Combine", module.TypeSystem.String,
            new TypeReference(Io, "Path", module, module.TypeSystem.CoreLibrary))
        {
            Parameters = { new ParameterDefinition(module.TypeSystem.String), new ParameterDefinition(module.TypeSystem.String) }
        };
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, folder);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, name);
        il.Emit(OpCodes.Call, combine);
    }

    // The constructor of a base class, with the parameters given, which the base class is known to have.
    private static MethodReference Constructor(TypeSystem system, TypeReference declaring, params TypeReference[] parameters)
    {
        MethodReference constructor = new MethodReference(".ctor", system.Void, declaring) { HasThis = true };
        foreach (TypeReference parameter in parameters)
        {
            constructor.Parameters.Add(new ParameterDefinition(parameter));
        }
        return constructor;
    }

    private static string Lower(string name)
    {
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    // ---- the file ----

    // What must not change but the bodies: the file kind and flags, and the strong-name signature.
    private static string Header(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(bytes, 0x3c);
        int optional = pe + 24;
        int count = BitConverter.ToUInt16(bytes, pe + 4 + 2);
        int sections = optional + BitConverter.ToUInt16(bytes, pe + 4 + 16);
        int directories = optional + (BitConverter.ToUInt16(bytes, optional) == 0x10b ? 96 : 112);
        int clr = Offset(bytes, count, sections, BitConverter.ToInt32(bytes, directories + 14 * 8));
        int signature = BitConverter.ToInt32(bytes, clr + 32 + 4);
        return bytes.Length.ToString() + " bytes, COR flags 0x" + BitConverter.ToInt32(bytes, clr + 16).ToString("x")
            + ", strong-name signature " + signature + " bytes";
    }

    // Where in the file an address of the image is: the section that holds it.
    private static int Offset(byte[] bytes, int count, int sections, int address)
    {
        for (int i = 0; i < count; i++)
        {
            int section = sections + i * 40;
            int start = BitConverter.ToInt32(bytes, section + 12);
            int raw = BitConverter.ToInt32(bytes, section + 16);
            if (address >= start && address < start + Math.Max(BitConverter.ToInt32(bytes, section + 8), raw))
            {
                return address - start + BitConverter.ToInt32(bytes, section + 20);
            }
        }
        throw new Exception("no section holds the address " + address);
    }

    // ---- smoke ----

    // The output takes the runtime's own place; what it refers to is taken from this runtime whatever
    // version it asks for (the output is built for the phone's, which is one ahead of the desktop's).
    private sealed class Apart : AssemblyLoadContext
    {
        private readonly string path;

        public Apart(string path) : base("unstub-watcher smoke")
        {
            this.path = path;
        }

        protected override Assembly Load(AssemblyName name)
        {
            return name.Name == "System.IO.FileSystem.Watcher" ? LoadFromAssemblyPath(path) : Assembly.Load(new AssemblyName(name.Name));
        }
    }

    // Where the handlers CameraPlus hooks would arrive (they never do).
    private sealed class Sink
    {
        public void Saw(string raised, object sender, object arguments)
        {
            Console.WriteLine("raised " + raised + ": " + arguments);
        }
    }

    private static Delegate Handler(EventInfo raised, Sink sink, string name)
    {
        // The delegate's type is the output's own: it is built as an expression tree over that type.
        ParameterExpression[] parameters = raised.EventHandlerType.GetMethod("Invoke").GetParameters()
            .Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        Expression call = Expression.Call(Expression.Constant(sink), typeof(Sink).GetMethod(nameof(Sink.Saw)),
            Expression.Constant(name), parameters[0], Expression.Convert(parameters[1], typeof(object)));
        return Expression.Lambda(raised.EventHandlerType, call, parameters).Compile();
    }

    private static int Smoke(string path)
    {
        Console.WriteLine(Path.GetFileName(path) + ": " + Header(path));
        var failures = new List<string>();
        void Check(string what, bool ok)
        {
            Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
            if (!ok)
            {
                failures.Add(what);
            }
        }

        path = Path.GetFullPath(path);
        Assembly assembly = new Apart(path).LoadFromAssemblyPath(path);
        Type watcherType = assembly.GetType(Io + ".FileSystemWatcher", true);
        Type changeTypes = assembly.GetType(Io + ".WatcherChangeTypes", true);
        Type notifyFilters = assembly.GetType(Io + ".NotifyFilters", true);
        object all = Enum.Parse(changeTypes, "All");
        string folder = Path.Combine(Path.GetTempPath(), "unstub-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // CameraPlus (Source/Assets.cs, LoadShaders), step by step.
            object watcher = Activator.CreateInstance(watcherType, folder, "*.png");
            // NotifyFilters.LastWrite | NotifyFilters.FileName
            watcherType.GetProperty("NotifyFilter").SetValue(watcher, Enum.ToObject(notifyFilters, 0x11));
            var sink = new Sink();
            foreach (string name in new[] { "Created", "Changed", "Renamed", "Deleted" })
            {
                EventInfo raised = watcherType.GetEvent(name);
                Delegate handler = Handler(raised, sink, name);
                raised.AddEventHandler(watcher, handler);
                raised.RemoveEventHandler(watcher, handler);
            }
            Check("made, set up and hooked as CameraPlus does", true);
            PropertyInfo raising = watcherType.GetProperty("EnableRaisingEvents");
            raising.SetValue(watcher, true);
            Check("EnableRaisingEvents set: kept, and nothing starts", (bool)raising.GetValue(watcher));
            Check("Path is given back", (string)watcherType.GetProperty("Path").GetValue(watcher) == folder);
            Check("Filter is given back", (string)watcherType.GetProperty("Filter").GetValue(watcher) == "*.png");
            Check("NotifyFilter is given back", Convert.ToInt32(watcherType.GetProperty("NotifyFilter").GetValue(watcher)) == 0x11);
            watcherType.GetMethod("BeginInit").Invoke(watcher, null);
            watcherType.GetMethod("EndInit").Invoke(watcher, null);
            watcherType.GetMethod("WaitForChanged", new[] { changeTypes, typeof(TimeSpan) })
                .Invoke(watcher, new[] { all, (object)TimeSpan.FromMilliseconds(30) });
            Check("BeginInit, EndInit and the TimeSpan wait run", true);
            ((IDisposable)watcher).Dispose();

            object plain = Activator.CreateInstance(watcherType);
            int count = (int)watcherType.GetProperty("Filters").PropertyType.GetProperty("Count")
                .GetValue(watcherType.GetProperty("Filters").GetValue(plain));
            Check("a new watcher has the desktop's defaults", (string)watcherType.GetProperty("Filter").GetValue(plain) == "*.*"
                && (int)watcherType.GetProperty("InternalBufferSize").GetValue(plain) == 8192
                && Convert.ToInt32(watcherType.GetProperty("NotifyFilter").GetValue(plain)) == 0x13
                && !(bool)watcherType.GetProperty("IncludeSubdirectories").GetValue(plain) && count == 0);
            watcherType.GetProperty("IncludeSubdirectories").SetValue(plain, true);
            watcherType.GetProperty("Site").SetValue(plain, null);
            watcherType.GetProperty("SynchronizingObject").SetValue(plain, null);
            Check("IncludeSubdirectories keeps what was set; Site and SynchronizingObject take null",
                (bool)watcherType.GetProperty("IncludeSubdirectories").GetValue(plain)
                && watcherType.GetProperty("Site").GetValue(plain) == null);

            Stopwatch watch = Stopwatch.StartNew();
            object waited = watcherType.GetMethod("WaitForChanged", new[] { changeTypes, typeof(int) })
                .Invoke(plain, new[] { all, (object)40 });
            watch.Stop();
            Type resultType = assembly.GetType(Io + ".WaitForChangedResult", true);
            Check("WaitForChanged(All, 40) waits the time given", watch.ElapsedMilliseconds >= 35);
            Check("and says: nothing changed, timed out", Convert.ToInt32(resultType.GetProperty("ChangeType").GetValue(waited)) == 0
                && (bool)resultType.GetProperty("TimedOut").GetValue(waited));

            object created = Enum.Parse(changeTypes, "Created");
            Type argsType = assembly.GetType(Io + ".FileSystemEventArgs", true);
            object arguments = Activator.CreateInstance(argsType, created, folder, "a.png");
            Check("FileSystemEventArgs keeps what it was given",
                (string)argsType.GetProperty("Name").GetValue(arguments) == "a.png"
                && (string)argsType.GetProperty("FullPath").GetValue(arguments) == Path.Combine(folder, "a.png")
                && Equals(argsType.GetProperty("ChangeType").GetValue(arguments), created));
            Type renamedType = assembly.GetType(Io + ".RenamedEventArgs", true);
            object renamed = Activator.CreateInstance(renamedType, created, folder, "b.png", "a.png");
            Check("RenamedEventArgs keeps the old name",
                (string)renamedType.GetProperty("OldName").GetValue(renamed) == "a.png"
                && (string)renamedType.GetProperty("OldFullPath").GetValue(renamed) == Path.Combine(folder, "a.png"));
            Type errorType = assembly.GetType(Io + ".ErrorEventArgs", true);
            Exception failure = new InvalidOperationException("watched folder gone");
            object error = Activator.CreateInstance(errorType, failure);
            Check("ErrorEventArgs gives the exception back",
                ReferenceEquals(errorType.GetMethod("GetException").Invoke(error, null), failure));
            Type overflowType = assembly.GetType(Io + ".InternalBufferOverflowException", true);
            Check("InternalBufferOverflowException carries its message",
                (string)((Exception)Activator.CreateInstance(overflowType, "full")).Message == "full"
                && Activator.CreateInstance(overflowType) is Exception);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
        Console.WriteLine(failures.Count == 0 ? "smoke: all passed" : "smoke: " + failures.Count + " failed: " + string.Join("; ", failures));
        return failures.Count == 0 ? 0 : 1;
    }
}
