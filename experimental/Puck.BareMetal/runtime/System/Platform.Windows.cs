// Hosted Windows platform glue: console output (via WriteFile so redirected/captured stdout works),
// fail-fast, time, threading, and the startup argument helpers. Active when PuckRuntimePlatform=WINDOWS.

#if WINDOWS

using System;
using System.Runtime;
using System.Runtime.InteropServices;

namespace System
{
    public static unsafe partial class Console
    {
        private const int STD_OUTPUT_HANDLE = -11;

        [DllImport("kernel32"), SuppressGCTransition]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32"), SuppressGCTransition]
        private static extern int WriteFile(IntPtr handle, void* buffer, uint count, uint* written, void* overlapped);

        [DllImport("kernel32"), SuppressGCTransition]
        private static extern int SetConsoleTextAttribute(IntPtr handle, ushort attributes);

        public static void Write(char value)
        {
            // ASCII byte via WriteFile: works for a console, a pipe, or a redirected file alike.
            byte b = (byte)value;
            uint written;
            WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), &b, 1, &written, null);
        }

        public static ConsoleColor ForegroundColor
        {
            set => SetConsoleTextAttribute(GetStdHandle(STD_OUTPUT_HANDLE), (ushort)value);
        }
    }

    public static unsafe partial class Environment
    {
        [DllImport("kernel32"), SuppressGCTransition]
        private static extern long GetTickCount64();

        [DllImport("kernel32"), SuppressGCTransition]
        private static extern void RaiseFailFastException(IntPtr exceptionRecord, IntPtr contextRecord, int flags);

        [DllImport("kernel32"), SuppressGCTransition]
        private static extern void ExitProcess(uint exitCode);

        public static long TickCount64 => GetTickCount64();

        public static void FailFast(string message) => RaiseFailFastException(default, default, 0);

        public static void Exit(int exitCode) => ExitProcess((uint)exitCode);
    }
}

namespace System.Threading
{
    public static class Thread
    {
        [DllImport("kernel32"), SuppressGCTransition]
        public static extern void Sleep(int millisecondsTimeout);
    }
}

namespace Internal.Runtime.CompilerHelpers
{
    internal static unsafe partial class StartupCodeHelpers
    {
        // The hosted process is started by the native PuckStart, not the CRT, so there is no argv.
        internal static void InitializeCommandLineArgsW(int argc, char** argv) { }

        internal static string[] GetMainMethodArguments() => new string[0];
    }
}

#endif
