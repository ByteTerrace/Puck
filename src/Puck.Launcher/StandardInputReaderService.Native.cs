using System.Runtime.InteropServices;

namespace Puck.Launcher;

public sealed partial class StandardInputReaderService {
    private const uint FileTypeDisk = 0x0001;
    private const uint FileTypePipe = 0x0003;
    private const short PollIn = 0x0001;
    private const int StandardInputHandle = -10;

    // Answers whether a read of redirected standard input would wait for a writer: false while bytes (or end of
    // input) are pending, true on an empty open pipe. A source this cannot classify answers true, so the backlog is
    // released rather than held on a guess. A regular file never waits.
    private static bool ReadWouldWait() {
        if (OperatingSystem.IsWindows()) {
            var handle = GetStdHandle(standardHandle: StandardInputHandle);

            return (GetFileType(file: handle) switch {
                FileTypeDisk => false,
                // A failed peek is a broken pipe: the writer closed it, so the read returns end of input at once.
                FileTypePipe => (PeekNamedPipe(
                    buffer: 0,
                    bufferSize: 0,
                    bytesLeftThisMessage: 0,
                    bytesRead: 0,
                    pipe: handle,
                    totalBytesAvailable: out var available
                ) && (available == 0U)),
                _ => true,
            });
        }

        if (
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS() ||
            OperatingSystem.IsFreeBSD()
        ) {
            var descriptor = new PollDescriptor {
                Descriptor = 0,
                Events = PollIn,
            };

            // Readable covers pending bytes and a hung-up writer alike; no ready descriptor is a read that waits, and a
            // failed poll is a source this cannot classify.
            return (Poll(
                count: 1,
                descriptors: ref descriptor,
                timeoutMilliseconds: 0
            ) != 1);
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor {
        public int Descriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileType")]
    private static partial uint GetFileType(nint file);
    [LibraryImport("kernel32.dll", EntryPoint = "GetStdHandle")]
    private static partial nint GetStdHandle(int standardHandle);
    [LibraryImport("kernel32.dll", EntryPoint = "PeekNamedPipe")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekNamedPipe(nint pipe, nint buffer, uint bufferSize, nint bytesRead, out uint totalBytesAvailable, nint bytesLeftThisMessage);
    [LibraryImport("libc", EntryPoint = "poll")]
    private static partial int Poll(ref PollDescriptor descriptors, nuint count, int timeoutMilliseconds);
}
