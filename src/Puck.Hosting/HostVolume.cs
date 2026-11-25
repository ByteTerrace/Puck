using System.Runtime.InteropServices;

namespace Puck.Hosting;

/// <summary>One reading of the volume a directory lies on.</summary>
/// <param name="TotalBytes">The volume's size, in bytes.</param>
/// <param name="AvailableBytes">The bytes the caller may still write to it, in bytes: the free space less whatever a
/// quota withholds.</param>
public readonly record struct HostVolumeReading(long TotalBytes, long AvailableBytes);
/// <summary>
/// Reads the size and free space of the volume a directory lies on: on Windows the directory's own free-space query,
/// which answers for a share or a mount point as well as a drive, elsewhere the file system's statistics. A cheap
/// operating-system query. A cache that keeps derived products on disk sizes itself from it, as a job sizes itself from
/// <see cref="HostMemory"/>.
/// </summary>
public static partial class HostVolume {
    [LibraryImport(libraryName: "kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(unmanagedType: UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);

    /// <summary>Reads the volume a directory lies on. A directory that does not exist yet is read through its nearest
    /// existing ancestor, which lies on the volume the directory will be made on.</summary>
    /// <param name="directory">The directory, absolute or relative to the working directory.</param>
    /// <returns>The reading, or <see langword="null"/> when no ancestor exists or the operating system refuses the
    /// query.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public static HostVolumeReading? Read(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        var existing = Path.GetFullPath(path: directory);

        while (!Directory.Exists(path: existing)) {
            if (Path.GetDirectoryName(path: Path.TrimEndingDirectorySeparator(path: existing)) is not { Length: > 0 } parent) { return null; }
            existing = parent;
        }
        try {
            if (OperatingSystem.IsWindows()) {
                // DriveInfo rejects UNC shares. The native query accepts a directory, including a share or mount point.
                var path = (Path.EndsInDirectorySeparator(path: existing) ? existing : (existing + Path.DirectorySeparatorChar));

                return (GetDiskFreeSpaceEx(available: out var available, directory: path, free: out _, total: out var total)
                    ? new HostVolumeReading(AvailableBytes: ((long)Math.Min(val1: available, val2: long.MaxValue)), TotalBytes: ((long)Math.Min(val1: total, val2: long.MaxValue)))
                    : null);
            }

            var drive = new DriveInfo(driveName: existing);

            return new HostVolumeReading(AvailableBytes: drive.AvailableFreeSpace, TotalBytes: drive.TotalSize);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException)) {
            return null;
        }
    }
}
