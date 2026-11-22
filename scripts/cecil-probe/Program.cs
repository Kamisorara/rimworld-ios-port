using System; using System.IO; using Mono.Cecil;
class T { static void Main(string[] a) {
  foreach (var f in Directory.GetFiles(a[0], "*.dll")) {
    try { var x = AssemblyDefinition.ReadAssembly(f); x.Dispose(); }
    catch (Exception e) { Console.WriteLine(Path.GetFileName(f) + " -> " + e.GetType().Name); }
  } } }