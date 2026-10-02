using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Puck.Testing;

/// <summary>Creates a directory link for a law that holds code to what it does through one: a symbolic link where the
/// host allows creating one, and otherwise, on Windows, an NTFS junction, which needs no privilege and which the file
/// system reports as a reparse point exactly as it does a directory symbolic link. A file link has no junction
/// equivalent, so a law that needs one still depends on the host allowing symbolic links.</summary>
public static partial class DirectoryLinks {
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint GenericWrite = 0x40000000;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint OpenExisting = 3;
    private const uint ShareAll = 7;

    /// <summary>Creates a link at <paramref name="link"/> to the directory <paramref name="target"/>.</summary>
    /// <param name="link">The link's path, which must not exist; its parent must.</param>
    /// <param name="target">The existing target directory's path.</param>
    /// <exception cref="IOException">Neither a symbolic link nor, on Windows, a junction could be created.</exception>
    /// <exception cref="UnauthorizedAccessException">The host refuses symbolic links and is not Windows.</exception>
    public static void Create(string link, string target) {
        var linkPath = Path.GetFullPath(path: link);
        var targetPath = Path.GetFullPath(path: target);

        try {
            _ = Directory.CreateSymbolicLink(path: linkPath, pathToTarget: targetPath);

            return;
        } catch (Exception exception) when (((exception is IOException or UnauthorizedAccessException) && OperatingSystem.IsWindows())) {
            // Creating a symbolic link needs a privilege or Developer Mode; a junction needs neither.
        }

        CreateJunction(link: linkPath, target: targetPath);
    }
    /// <summary>Removes a link made by <see cref="Create"/>, leaving its target untouched.</summary>
    /// <param name="link">The link's path.</param>
    public static void Remove(string link) {
        if (new DirectoryInfo(path: link).LinkTarget is not null) {
            Directory.Delete(path: link);
        }
    }

    // A mount-point reparse buffer: tag, data length, reserved, then the substitute and print names' offsets and lengths
    // in bytes, then the two names, each followed by a terminating null.
    private static void CreateJunction(string link, string target) {
        _ = Directory.CreateDirectory(path: link);

        using var handle = OpenDirectory(
            access: GenericWrite,
            disposition: OpenExisting,
            flags: FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            name: link,
            security: 0,
            share: ShareAll,
            template: 0
        );

        if (handle.IsInvalid) {
            throw new IOException(message: $"'{link}' cannot be opened to make a junction (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        var substitute = Encoding.Unicode.GetBytes(s: ("\\??\\" + target));
        var print = Encoding.Unicode.GetBytes(s: target);
        var buffer = new byte[((20 + substitute.Length) + print.Length)];

        _ = BitConverter.TryWriteBytes(destination: buffer.AsSpan(start: 0), value: IoReparseTagMountPoint);
        _ = BitConverter.TryWriteBytes(destination: buffer.AsSpan(start: 4), value: checked((ushort)(buffer.Length - 8)));
        _ = BitConverter.TryWriteBytes(destination: buffer.AsSpan(start: 10), value: checked((ushort)substitute.Length));
        _ = BitConverter.TryWriteBytes(destination: buffer.AsSpan(start: 12), value: checked((ushort)(substitute.Length + 2)));
        _ = BitConverter.TryWriteBytes(destination: buffer.AsSpan(start: 14), value: checked((ushort)print.Length));
        substitute.CopyTo(array: buffer, index: 16);
        print.CopyTo(array: buffer, index: (18 + substitute.Length));

        if (!SetReparse(
            code: FsctlSetReparsePoint,
            handle: handle,
            input: buffer,
            inputSize: ((uint)buffer.Length),
            output: 0,
            outputSize: 0,
            overlapped: 0,
            returned: out _
        )) {
            throw new IOException(message: $"'{link}' cannot be made a junction to '{target}' (Win32 error {Marshal.GetLastPInvokeError()}).");
        }
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenDirectory(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetReparse(SafeFileHandle handle, uint code, byte[] input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
}
