using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Minicloud.Worker.Tests;

internal static class ScratchPoolProbe
{
    [ModuleInitializer]
    internal static void Start()
    {
        new Thread(() =>
        {
            while (true)
            {
                var sw = Stopwatch.StartNew();
                using var done = new ManualResetEventSlim();
                ThreadPool.UnsafeQueueUserWorkItem(_ => done.Set(), null);
                done.Wait();
                var lag = sw.ElapsedMilliseconds;
                if (lag > 500)
                {
                    var stacks = "";
                    File.AppendAllText("/tmp/poollag.log",
                        $"{DateTime.UtcNow:O} lag={lag}ms threads={ThreadPool.ThreadCount} pending={ThreadPool.PendingWorkItemCount}\n");
                }
                Thread.Sleep(200);
            }
        }) { IsBackground = true, Name = "pool-probe" }.Start();
    }
}
