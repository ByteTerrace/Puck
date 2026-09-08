using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Puck.Networking;

public static partial class LocalUserAccess {
    /// <summary>Opens a regular Linux capability file, checking the opened inode's owner, link count and private permissions.</summary>
    /// <param name="path">The descriptor path. Symbolic links are refused.</param>
    /// <param name="create">Creates a new 0600 file when true; opens an existing file read-only otherwise.</param>
    /// <returns>An owned stream whose inode was validated before any content is read.</returns>
    /// <exception cref="PlatformNotSupportedException">The platform is not Linux x64.</exception>
    /// <exception cref="UnauthorizedAccessException">The inode is shared, nonregular, or owned by another user.</exception>
    /// <exception cref="IOException">The file cannot be opened or inspected.</exception>
    [SupportedOSPlatform("linux")]
    public static FileStream OpenLinuxFile(string path, bool create) {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) { throw new PlatformNotSupportedException("Linux capability files require x64."); }
        // Same openat/fstat discipline as confined storage: inspect the actual inode, never a pre-open path.
        const int noFollow = 0x20000, closeOnExec = 0x80000, nonBlock = 0x800;
        var fd = OpenAt(-100, path, noFollow | closeOnExec | nonBlock | (create ? 2 | 0x40 | 0x80 : 0), 0x180);
        if (fd < 0) { throw new IOException("Cannot open private capability file.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError())); }
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try {
            if (Fstat(handle, out var info) != 0) { throw new IOException("Cannot inspect private capability inode."); }
            if (info.Owner != GetEuid() || info.Links != 1 || (info.Mode & 0xF000) != 0x8000 || (info.Mode & 0x3F) != 0) {
                throw new UnauthorizedAccessException("Capability file must be a private, singly linked regular file owned by the current user.");
            }
            return new FileStream(handle, create ? FileAccess.ReadWrite : FileAccess.Read);
        } catch { handle.Dispose(); throw; }
    }

    // Linux x86-64 struct stat; other ABIs are refused before interop.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct LinuxFileInformation {
        [FieldOffset(16)] public ulong Links;
        [FieldOffset(24)] public uint Mode;
        [FieldOffset(28)] public uint Owner;
    }
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(int directory, string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int Fstat(SafeFileHandle file, out LinuxFileInformation info);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();
}
