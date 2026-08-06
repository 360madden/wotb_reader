using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
public static class CounterTarget {
    // Static field: stable address in the managed data/heap, NOT the stack.
    // Memory breakpoints on stack pages are pathological (every stack op fires
    // the guard); a static address keeps the write surface to exactly one int.
    public static int Counter = 0;
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    public static unsafe void Main() {
        fixed (int* p = &Counter) {
            *p = 0;
            File.WriteAllText(@"C:\Users\mrkoo\AppData\Local\Temp\wt-counter-addr.txt", ((long)p).ToString("X8"));
            File.WriteAllText(@"C:\Users\mrkoo\AppData\Local\Temp\wt-counter-tid.txt", GetCurrentThreadId().ToString());
            long n = 0;
            while (true) {
                (*p)++;
                n++;
                if ((n % 40) == 0) File.WriteAllText(@"C:\Users\mrkoo\AppData\Local\Temp\wt-counter-progress.txt", (*p).ToString());
                Thread.Sleep(1);
            }
        }
    }
}
