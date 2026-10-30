using System.IO.MemoryMappedFiles;
using Puck.Launcher.Release;

namespace Puck.Launcher.Tests.Release;

/// <summary>A file this test process maps and keeps open, standing in for the image a launched process was loaded
/// from: the operating system reports the mapping exactly as it reports a loaded assembly's, so
/// <see cref="InstallRegistration.Register"/> confirms its identity the same way, and the file stays open as the .NET
/// runtime keeps a loaded assembly's file open (which on Windows denies renaming or deleting its directory).</summary>
internal sealed unsafe class MappedImage : IDisposable {
    private readonly FileStream m_stream;
    private readonly MemoryMappedFile m_file;
    private readonly MemoryMappedViewAccessor m_view;

    public MappedImage(string path) {
        m_stream = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: path, share: (FileShare.Read | FileShare.Delete));
        m_file = MemoryMappedFile.CreateFromFile(access: MemoryMappedFileAccess.Read, capacity: 0, fileStream: m_stream,
            inheritability: HandleInheritability.None, leaveOpen: true, mapName: null);
        m_view = m_file.CreateViewAccessor(access: MemoryMappedFileAccess.Read, offset: 0, size: 0);
        byte* pointer = null;

        m_view.SafeMemoryMappedViewHandle.AcquirePointer(pointer: ref pointer);
        Address = ((nint)pointer);
        Path = path;
    }

    public nint Address { get; }
    public string Path { get; }

    /// <summary>Registers a process loaded from <paramref name="imagePath"/> (by default this mapping's own path).</summary>
    public InstallRegistration Register(string installRoot, string? imagePath = null) => InstallRegistration.Register(
        cancellationToken: Xunit.TestContext.Current.CancellationToken, imageAddress: Address, imagePath: (imagePath ?? Path), installRoot: installRoot);

    public void Dispose() {
        m_view.SafeMemoryMappedViewHandle.ReleasePointer();
        m_view.Dispose();
        m_file.Dispose();
        m_stream.Dispose();
    }
}
