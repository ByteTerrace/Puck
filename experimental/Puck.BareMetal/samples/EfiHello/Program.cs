// Puck.BareMetal — UEFI hello on the Puck.Runtime core library.
//
// The native kernel entry (compat/native/puck-efi.c: EfiEntry) brings up the machine, exits boot
// services, and calls this managed Main. Firmware services (incl. the ConOut console) are gone by
// then, so output goes through the kernel's own serial port. This doubles as the smoke test that
// Puck.Runtime runs managed code bare-metal: P/Invoke, strings, and heap allocation.

using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("puckefi"), SuppressGCTransition]
    private static extern void PuckSerialWriteByte(int b);

    private static void Print(string s)
    {
        for (int i = 0; i < s.Length; i++)
            PuckSerialWriteByte((byte)s[i]);
        PuckSerialWriteByte('\r');
        PuckSerialWriteByte('\n');
    }

    private static int Main()
    {
        Print("[hello] Puck.Runtime is live.");

        // Exercise the allocator + array + foreach so a runtime ABI bug would surface here.
        int[] values = new int[8];
        int sum = 0;
        for (int i = 0; i < values.Length; i++)
            values[i] = i + 1;
        foreach (int v in values)
            sum += v;

        Print(sum == 36 ? "[hello] alloc + array + foreach OK (sum=36)." : "[hello] RUNTIME FAILURE.");
        return 0;
    }
}
