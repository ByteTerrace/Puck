using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Puck.Platform.Windows.Interop;

internal static partial class Kernel32 {
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalFree(nint memoryHandle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalLock(nint memoryHandle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memoryHandle);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    public static partial SafeWaitHandle CreateWaitableTimerEx(nint timerAttributes, nint timerName, uint flags, uint desiredAccess);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(SafeWaitHandle timerHandle, in long dueTime, int period, nint completionRoutine, nint completionRoutineArgument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint GetModuleHandle(string? moduleName);
}
