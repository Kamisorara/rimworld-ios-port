// 见文件名；构建运行：dotnet run（本地，net10）
// v9 落点抽检：UIRoot_Entry.Init 首指令应 call PortPlatform.OnEntryUIInit；
// 0Harmony 里 Type::GetField 的调用应改道 PortPlatform.GetFieldLax。
using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

class V
{
    static void Main(string[] a)
    {
        var asm = AssemblyDefinition.ReadAssembly(Path.Combine(a[0], "Assembly-CSharp.dll"));
        var init = asm.MainModule.GetType("Verse.UIRoot_Entry").Methods.First(m => m.Name == "Init");
        var first = init.Body.Instructions[0];
        Console.WriteLine("UIRoot_Entry.Init 首指令: " + first.OpCode + " " + first.Operand);
        var tc = asm.MainModule.GetType("Verse.TimeControls");
        Console.WriteLine("TimeControls 找到: " + (tc != null) + "；TimeButSize initonly: " +
            (tc == null ? "?" : tc.Fields.Where(f => f.Name == "TimeButSize").Select(f => f.IsInitOnly.ToString()).FirstOrDefault()));

        var h = AssemblyDefinition.ReadAssembly(Path.Combine(a[0], "0Harmony.dll"));
        int lax = 0;
        foreach (var t in h.MainModule.GetTypes())
            foreach (var m in t.Methods)
                if (m.HasBody)
                    foreach (var i in m.Body.Instructions)
                        if (i.Operand is MethodReference r && r.DeclaringType.FullName == "PortHooks.PortPlatform")
                            lax++;
        Console.WriteLine("0Harmony 内 PortPlatform 调用点: " + lax);
    }
}
