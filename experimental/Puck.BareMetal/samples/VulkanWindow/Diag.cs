// Puck.BareMetal VulkanWindow — tiny freestanding diagnostics (stdout via WriteFile).
using System;
using System.Runtime.InteropServices;

namespace Puck.BareMetal.VulkanWindow;

internal static unsafe class Diag {
    private const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32"), SuppressGCTransition]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32"), SuppressGCTransition]
    private static extern int WriteFile(IntPtr file, byte* buffer, int bytes, int* written, IntPtr overlapped);
    private static void WriteRaw(byte* buffer, int length) {
        int written;

        WriteFile(GetStdHandle(handle: STD_OUTPUT_HANDLE), buffer, length, &written, default);
    }

    public static void Log(string message) {
        byte* buffer = stackalloc byte[512];
        int length = message.Length;

        if (length > 510) length = 510;
        for (int i = 0; (i < length); i++)
            buffer[i] = (byte)message[i];
        buffer[length] = (byte)'\n';
        WriteRaw(buffer: buffer, length: (length + 1));
    }

    // Log "<label> <value>\n" without depending on BCL number formatting.
    public static void LogNum(string label, long value) {
        byte* buffer = stackalloc byte[512];
        int p = 0;
        int labelLen = label.Length;

        if (labelLen > 480) labelLen = 480;
        for (int i = 0; (i < labelLen); i++)
            buffer[p++] = (byte)label[i];
        buffer[p++] = (byte)' ';

        bool negative = (value < 0);
        ulong magnitude = (negative ? (ulong)(-value) : (ulong)value);

        byte* digits = stackalloc byte[20];
        int d = 0;

        do {
            digits[d++] = (byte)('0' + (int)(magnitude % 10));
            magnitude /= 10;
        }
        while (magnitude != 0);

        if (negative) buffer[p++] = (byte)'-';
        while (d > 0) buffer[p++] = digits[--d];
        buffer[p++] = (byte)'\n';
        WriteRaw(buffer: buffer, length: p);
    }
}
