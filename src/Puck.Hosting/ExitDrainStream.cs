using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Puck.Hosting;

// Cancellation ends the wait for more pipe data, not decoding of bytes already read. Once released, take one
// snapshot of the kernel buffer and consume it before reporting EOF. No other reader may consume this pipe.
internal sealed partial class ExitDrainStream : Stream {
    private readonly Stream m_pipe;
    private readonly StreamReader m_reader;
    private readonly CancellationToken m_release;
    // Process uses a 4096-byte FileStream buffer on Windows. Full-sized reads bypass its read-ahead buffer, so
    // every unread byte remains either here, in the text decoder, or in the kernel buffer we can measure.
    private readonly byte[] m_buffer = new byte[4096];
    private int m_offset;
    private int m_count;
    private int? m_remaining;

    internal ExitDrainStream(StreamReader reader, CancellationToken release) {
        m_reader = reader;
        m_pipe = reader.BaseStream;
        m_release = release;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) {
        if (disposing) { m_reader.Dispose(); }
        base.Dispose(disposing: disposing);
    }
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer: buffer.AsMemory(start: offset, length: count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer: buffer.AsMemory(start: offset, length: count), cancellationToken: cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) { return 0; }
        if (m_count == 0) {
            m_offset = 0;
            if (!m_release.IsCancellationRequested) {
                try {
                    m_count = await m_pipe.ReadAsync(buffer: m_buffer.AsMemory(), cancellationToken: m_release).ConfigureAwait(continueOnCapturedContext: false);
                    if (m_count == 0) { return 0; }
                } catch (OperationCanceledException) when (m_release.IsCancellationRequested) {
                    // The pending byte read has settled; the pipe can now be inspected without racing that read.
                }
            }
            if (m_count == 0) {
                m_remaining ??= AvailableBytes();
                if (m_remaining == 0) { return 0; }
                var read = await m_pipe.ReadAsync(buffer: m_buffer.AsMemory(), cancellationToken: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);

                m_count = Math.Min(val1: read, val2: m_remaining.Value);
                m_remaining -= m_count;
            }
        }
        var copied = Math.Min(val1: buffer.Length, val2: m_count);

        m_buffer.AsMemory(start: m_offset, length: copied).CopyTo(destination: buffer);
        m_offset += copied;
        m_count -= copied;
        return copied;
    }

    private int AvailableBytes() {
        if (OperatingSystem.IsWindows()) {
            var handle = ((FileStream)m_pipe).SafeFileHandle;
            if (PeekNamedPipe(handle: handle, buffer: 0, bufferSize: 0, bytesRead: 0, available: out var available, bytesLeft: 0) != 0) {
                return checked((int)available);
            }
            var error = Marshal.GetLastPInvokeError();

            if (error == 109) { return 0; } // ERROR_BROKEN_PIPE: every writer closed, with no buffered bytes left.
            throw new IOException(message: "Cannot inspect the child's output pipe.", innerException: new Win32Exception(error: error));
        }
        var pipeHandle = ((PipeStream)m_pipe).SafePipeHandle;
        // FIONREAD is an int-sized result on Unix; Linux and the BSD family assign different request numbers.
        var request = (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) ? 0x541Bu : 0x4004667Fu;

        if (Ioctl(handle: pipeHandle, request: request, available: out var count) == 0) { return count; }
        throw new IOException(message: "Cannot inspect the child's output pipe.", innerException: new Win32Exception(error: Marshal.GetLastPInvokeError()));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int PeekNamedPipe(Microsoft.Win32.SafeHandles.SafeFileHandle handle, nint buffer, uint bufferSize, nint bytesRead, out uint available, nint bytesLeft);
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(Microsoft.Win32.SafeHandles.SafePipeHandle handle, nuint request, out int available);
}
