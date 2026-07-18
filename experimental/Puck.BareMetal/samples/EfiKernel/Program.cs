// Part of Puck.BareMetal. UEFI kernel: self-hosted managed code after ExitBootServices.
//
// By the time Main runs, the native EfiEntry (compat/native/puck-efi.c) has already called
// ExitBootServices: there is no firmware, no boot services, no ConOut. This managed code runs on
// OUR memory (a bump allocator over conventional RAM) and prints through OUR console (a COM1
// 16550 serial driver we program directly). Every `new` below comes from our heap; every line
// is emitted by our UART.

using System;
using System.Buffers;
using System.Runtime.InteropServices;

// Output channel we own: the native COM1 serial driver (works after the firmware is gone). It
// writes a byte at a time so no managed stack buffer is needed (a stackalloc would draw a /GS
// stack cookie -> __security_cookie, which has no definition under the freestanding /NODEFAULTLIB
// link).
internal static class Serial {
    [DllImport("puckefi"), SuppressGCTransition]
    private static extern void PuckSerialWriteByte(int b);

    public static void Write(string s) {
        for (int i = 0; (i < s.Length); i++)
            PuckSerialWriteByte(b: (byte)s[i]);
    }
    public static void WriteLine(string s) {
        Write(s: s);
        PuckSerialWriteByte(b: '\r');
        PuckSerialWriteByte(b: '\n');
    }
    public static void WriteUInt(int value) {
        if (value >= 10)
            WriteUInt(value: (value / 10));
        PuckSerialWriteByte(b: ('0' + (value % 10)));
    }
}
internal static class Program {
    [DllImport("puckefi"), SuppressGCTransition]
    private static extern ulong PuckHeapUsed();
    private static int Main() {
        Serial.WriteLine(s: "[managed] self-hosted managed code is running.");

        // A large array straight from our bump allocator: must be zeroed, then survive a
        // write/read round-trip over the whole buffer.
        byte[] buffer = new byte[8192];
        bool ok = true;

        for (int i = 0; (i < buffer.Length); i++)
            if (buffer[i] != 0) { ok = false; break; }
        for (int i = 0; (i < buffer.Length); i++)
            buffer[i] = (byte)((i * 31) + 7);
        for (int i = 0; (i < buffer.Length); i++)
            if (buffer[i] != (byte)((i * 31) + 7)) { ok = false; break; }
        Serial.Write(s: "[managed] 8 KB heap buffer zeroed + round-trip: ");
        Serial.WriteLine(s: (ok ? "OK" : "FAIL"));

        // An int[] computed on our heap.
        int[] numbers = new int[100];

        for (int i = 0; (i < numbers.Length); i++)
            numbers[i] = i;
        int sum = 0;

        for (int i = 0; (i < numbers.Length); i++)
            sum += numbers[i];
        Serial.Write(s: "[managed] sum(0..99) = ");
        Serial.WriteUInt(value: sum);
        Serial.WriteLine(s: " (expected 4950)");

        // Many object allocations to exercise the allocator under churn.
        int allocated = 0;

        for (int i = 0; (i < 2000); i++)
            if (new object() is not null)
                allocated++;
        Serial.Write(s: "[managed] allocated objects: ");
        Serial.WriteUInt(value: allocated);
        Serial.WriteLine(s: "");

        // ArrayPool under no-GC: this is the payoff. Our bump allocator never frees, so without
        // pooling every transient buffer grows the heap forever. Warm the pool, then rent+return
        // a 4 KB buffer 10,000 times: the SAME backing array is recycled, so the heap watermark
        // barely moves. Contrast with 100 raw allocations, which the heap can never reclaim.
        ArrayPool<byte>.Shared.Return(ArrayPool<byte>.Shared.Rent(minimumLength: 4096)); // warm up (one-time setup)

        int pooledBefore = (int)PuckHeapUsed();

        for (int i = 0; (i < 10000); i++) {
            byte[] rented = ArrayPool<byte>.Shared.Rent(minimumLength: 4096);

            rented[0] = (byte)i;
            rented[(rented.Length - 1)] = (byte)(i ^ 0xFF);
            ArrayPool<byte>.Shared.Return(rented);
        }
        int pooledGrowth = ((int)PuckHeapUsed() - pooledBefore);

        Serial.Write(s: "[managed] 10000 pooled 4KB rent/return grew the heap by ");
        Serial.WriteUInt(value: pooledGrowth);
        Serial.WriteLine(s: " bytes");

        int rawBefore = (int)PuckHeapUsed();

        for (int i = 0; (i < 100); i++) {
            byte[] fresh = new byte[4096];

            fresh[0] = (byte)i;
        }
        int rawGrowth = ((int)PuckHeapUsed() - rawBefore);

        Serial.Write(s: "[managed] 100 raw 4KB allocations grew the heap by ");
        Serial.WriteUInt(value: rawGrowth);
        Serial.WriteLine(s: " bytes (no GC -> never reclaimed)");

        Serial.WriteLine(s: "[managed] Puck.BareMetal is now an operating system with pooled memory.");
        return 0;
    }
}
