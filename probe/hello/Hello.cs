// M1 托管探针：验证 Reflection.Emit 可用 + 用执行速度区分 JIT 与解释器。
// 结论回传走 host_log（[DllImport("__Internal")]，进主程序包的可执行文件），
// 不依赖 Console 的 stdout 在 iOS 上可见。
using System;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

internal static class Hello
{
    [DllImport("__Internal", EntryPoint = "host_log")]
    private static extern void HostLog(string message);

    private static int Main()
    {
        Log($".NET {Environment.Version}，{(Environment.Is64BitProcess ? "64" : "32")} 位进程");
        Log($"OS：{Environment.OSVersion}");
        try
        {
            int ms = JitProbe();
            Log(ms < 500
                ? $"结果：JIT 生效（机器码速度：{ms} ms）"
                : $"结果：疑似解释器（{ms} ms，慢于机器码一个数量级以上）");
            return 42;
        }
        catch (Exception ex)
        {
            Log("探针异常：" + ex);
            return 3;
        }
    }

    private static void Log(string s) => HostLog(s);

    // 发出 double Sum(int n)：r=0; for(i=0;i<n;i++) r+=i*0.5; return r
    // JIT 下 1000 万次迭代约几十毫秒；解释器下通常数秒到数十秒。
    private static int JitProbe()
    {
        var dm = new DynamicMethod("Sum", typeof(double), new[] { typeof(int) });
        ILGenerator il = dm.GetILGenerator();
        LocalBuilder r = il.DeclareLocal(typeof(double)); // loc0
        LocalBuilder i = il.DeclareLocal(typeof(int));    // loc1
        Label loop = il.DefineLabel();
        Label done = il.DefineLabel();

        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc_1);
        il.MarkLabel(loop);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Bge_S, done);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ldc_R8, 0.5);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc_1);
        il.Emit(OpCodes.Br_S, loop);
        il.MarkLabel(done);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);

        var sum = (Func<int, double>)dm.CreateDelegate(typeof(Func<int, double>));

        Log("Reflection.Emit：已生成动态方法");
        double warm = sum(1_000_000);
        Log($"预热（100 万次）：{warm:F1}");

        var sw = Stopwatch.StartNew();
        double result = sum(10_000_000);
        sw.Stop();
        Log($"正式（1000 万次）：结果 {result:F1}，耗时 {sw.ElapsedMilliseconds} ms");
        return (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue);
    }
}
