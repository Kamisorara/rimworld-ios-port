// 编译前置（PortMobile 的构建引用需要）：把给定 dll 里全部"static readonly 无常量"字段的
// initonly 摘掉——与安卓 patch.dll 对 staged 游戏做的同一件事（Patch.cs 行 297-308），也是
// iOS 设备端 setup v9 对游戏程序集要做的同一件事（PortMobile 写 Dialog_Options.UIScales、
// TimeControls.TimeButSize 等字段，CoreCLR 对已初始化类型的 initonly 静态字段写入抛
// FieldAccessException，Mono/Ludeon fork 放行）。in-place 写回（临时文件替换）。
// 用法：dotnet run --project scripts/relax-initonly -- <dir>
using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

string dir = args[0];
foreach (string file in Directory.GetFiles(dir, "*.dll").OrderBy(f => f))
{
    string name = Path.GetFileName(file);
    try
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dir);
        var ap = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { InMemory = true, ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
        int n = 0;
        foreach (TypeDefinition t in ap.MainModule.GetTypes())
            foreach (FieldDefinition f in t.Fields)
                if (f.IsStatic && f.IsInitOnly && !f.HasConstant) { f.IsInitOnly = false; n++; }
        if (n == 0) { Console.WriteLine($"{name}: 0"); continue; }
        string tmp = file + ".relax";
        ap.Write(tmp);
        ap.Dispose();
        File.Move(tmp, file, overwrite: true);
        Console.WriteLine($"{name}: {n} 字段摘除 initonly");
    }
    catch (Exception e) { Console.WriteLine($"{name}: 跳过（{e.GetType().Name}: {e.Message}）"); }
}
