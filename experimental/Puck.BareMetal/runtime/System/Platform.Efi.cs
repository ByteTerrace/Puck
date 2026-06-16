// UEFI platform glue: the firmware tables (from the UEFI spec), console output, fail-fast, time, and
// the managed entry the firmware/native bootstrap calls. Active when PuckRuntimePlatform=UEFI.

#if UEFI

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Internal.Runtime.CompilerHelpers;

namespace System
{
    public unsafe partial class Object
    {
        // The firmware system table is captured by the native entry (compat/native/puck-efi.c:
        // EfiEntry) and read back through this statically-bound getter.
        [DllImport("puckefi"), SuppressGCTransition]
        private static extern void* PuckGetEfiSystemTable();

        internal static EFI_SYSTEM_TABLE* EfiSystemTable => (EFI_SYSTEM_TABLE*)PuckGetEfiSystemTable();
    }

    public static unsafe partial class Console
    {
        public static void Write(char value)
        {
            // A 4-byte int holds the char in its low 16 bits; the zero high half is the CHAR16 NUL
            // terminator. (Avoids stackalloc, which would pull in the /GS __security_cookie.)
            int cc = value;
            EFI_SYSTEM_TABLE* st = Object.EfiSystemTable;
            st->ConOut->OutputString(st->ConOut, (char*)&cc);
        }

        public static ConsoleColor ForegroundColor
        {
            set
            {
                EFI_SYSTEM_TABLE* st = Object.EfiSystemTable;
                st->ConOut->SetAttribute(st->ConOut, (uint)value);
            }
        }
    }

    public static unsafe partial class Environment
    {
        public static void FailFast(string message)
        {
            EFI_SYSTEM_TABLE* st = Object.EfiSystemTable;
            fixed (char* p = message ?? "FailFast")
                st->ConOut->OutputString(st->ConOut, p);
            while (true) ;
        }

        public static long TickCount64
        {
            get
            {
                EFI_TIME time;
                Object.EfiSystemTable->RuntimeServices->GetTime(&time, null);
                long days = time.Year * 365 + time.Month * 31 + time.Day;
                long seconds = days * 24 * 60 * 60 + time.Hour * 60 * 60 + time.Minute * 60 + time.Second;
                return seconds * 1000 + time.Nanosecond / 1000000;
            }
        }
    }
}

namespace Internal.Runtime.CompilerHelpers
{
    internal static unsafe partial class StartupCodeHelpers
    {
        [RuntimeImport("*", "__managed__Main")]
        [MethodImpl(MethodImplOptions.InternalCall)]
        private static extern int ManagedMain(int argc, char** argv);

        // The managed entry. In this build the native EfiEntry is the PE entry point and calls
        // __managed__Main directly; this export is retained for completeness.
        [RuntimeExport("EfiMain")]
        private static long EfiMain(IntPtr imageHandle, EFI_SYSTEM_TABLE* systemTable)
        {
            ManagedMain(0, null);
            while (true) ;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EFI_HANDLE { private IntPtr _handle; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL
    {
        private readonly IntPtr _reset;
        public readonly delegate* unmanaged<void*, char*, void*> OutputString;
        private readonly IntPtr _testString;
        private readonly IntPtr _queryMode;
        private readonly IntPtr _setMode;
        public readonly delegate* unmanaged<void*, uint, void> SetAttribute;
        private readonly IntPtr _clearScreen;
        public readonly delegate* unmanaged<void*, uint, uint, void> SetCursorPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct EFI_INPUT_KEY { public readonly ushort ScanCode; public readonly ushort UnicodeChar; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_SIMPLE_TEXT_INPUT_PROTOCOL
    {
        private readonly IntPtr _reset;
        public readonly delegate* unmanaged<void*, EFI_INPUT_KEY*, ulong> ReadKeyStroke;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct EFI_TABLE_HEADER
    {
        public readonly ulong Signature;
        public readonly uint Revision;
        public readonly uint HeaderSize;
        public readonly uint Crc32;
        public readonly uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_SYSTEM_TABLE
    {
        public readonly EFI_TABLE_HEADER Hdr;
        public readonly char* FirmwareVendor;
        public readonly uint FirmwareRevision;
        public readonly EFI_HANDLE ConsoleInHandle;
        public readonly EFI_SIMPLE_TEXT_INPUT_PROTOCOL* ConIn;
        public readonly EFI_HANDLE ConsoleOutHandle;
        public readonly EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* ConOut;
        public readonly EFI_HANDLE StandardErrorHandle;
        public readonly EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* StdErr;
        public readonly EFI_RUNTIME_SERVICES* RuntimeServices;
        public readonly EFI_BOOT_SERVICES* BootServices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EFI_TIME
    {
        public ushort Year;
        public byte Month;
        public byte Day;
        public byte Hour;
        public byte Minute;
        public byte Second;
        public byte Pad1;
        public uint Nanosecond;
        public short TimeZone;
        public byte Daylight;
        public byte Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_RUNTIME_SERVICES
    {
        public readonly EFI_TABLE_HEADER Hdr;
        public readonly delegate* unmanaged<EFI_TIME*, void*, ulong> GetTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe readonly struct EFI_BOOT_SERVICES
    {
        private readonly EFI_TABLE_HEADER _hdr;
        private readonly void* _p0, _p1, _p2, _p3, _p4;
        // AllocatePool is the 6th boot-services entry after the header.
        public readonly delegate* unmanaged<int, nint, void**, ulong> AllocatePool;
    }
}

#endif
