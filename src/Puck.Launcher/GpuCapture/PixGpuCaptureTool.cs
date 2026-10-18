using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;

namespace Puck.Launcher;

/// <summary>
/// PIX on Windows' programmatic GPU capture as a <see cref="IGpuCaptureTool"/>: the newest installed
/// <c>WinPixGpuCapturer.dll</c> (under <c>Program Files/Microsoft PIX/&lt;version&gt;</c>, the library pix3's
/// <c>PIXLoadLatestWinPixGpuCapturerLibrary</c> loads), loaded before the Direct3D 12 device is created so PIX sees it
/// from its creation. A capture spans every frame between <c>BeginProgrammaticGpuCapture</c> and
/// <c>EndProgrammaticGpuCapture</c> and is written to the <c>.wpix</c> path it was started with; the end answers
/// <c>E_PENDING</c> while PIX is still writing, and is asked again on the next frame. PIX captures Direct3D 12 only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class PixGpuCaptureTool : IGpuCaptureTool {
    /// <summary>The capturer library's file name.</summary>
    public const string LibraryName = "WinPixGpuCapturer.dll";

    private const int Pending = unchecked((int)0x8000000A);

    private readonly delegate* unmanaged[Stdcall]<void*, int> m_begin;
    private readonly delegate* unmanaged[Stdcall]<int, int> m_end;

    private string? m_path;

    private PixGpuCaptureTool(nint begin, nint end) {
        m_begin = ((delegate* unmanaged[Stdcall]<void*, int>)begin);
        m_end = ((delegate* unmanaged[Stdcall]<int, int>)end);
    }

    /// <inheritdoc/>
    public bool CapturesSpanFrames => true;
    /// <inheritdoc/>
    public string Name => "pix";

    /// <summary>Loads the newest installed PIX GPU capturer into the process, before the Direct3D 12 device is
    /// created.</summary>
    /// <param name="tool">The loaded tool, or <see langword="null"/> when it could not be loaded.</param>
    /// <param name="failure">Why it could not be loaded, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the tool loaded.</returns>
    public static bool TryLoad(out PixGpuCaptureTool? tool, out string? failure) {
        tool = null;

        var root = Path.Combine(Environment.GetFolderPath(folder: Environment.SpecialFolder.ProgramFiles), "Microsoft PIX");
        var library = (Directory.Exists(path: root)
            ? Directory.EnumerateDirectories(path: root)
                .Select(selector: static directory => Path.Combine(directory, LibraryName))
                .Where(predicate: File.Exists)
                .OrderBy(keySelector: static path => (Version.TryParse(input: Path.GetFileName(path: Path.GetDirectoryName(path: path)), result: out var version) ? version : new Version()))
                .LastOrDefault()
            : null);

        if (library is null) {
            failure = $"PIX is not installed (no {LibraryName} under {root})";

            return false;
        }

        if (!NativeLibrary.TryLoad(libraryPath: library, handle: out var handle)) {
            failure = $"{library} did not load";

            return false;
        }

        if (
            !NativeLibrary.TryGetExport(handle: handle, name: "BeginProgrammaticGpuCapture", address: out var begin) ||
            !NativeLibrary.TryGetExport(handle: handle, name: "EndProgrammaticGpuCapture", address: out var end)
        ) {
            failure = $"{library} exports no programmatic GPU capture";

            return false;
        }

        tool = new PixGpuCaptureTool(begin: begin, end: end);
        failure = null;

        return true;
    }

    /// <inheritdoc/>
    public string? Start(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        m_path = Path.ChangeExtension(path: path, extension: ".wpix");

        // PIXCaptureParameters is a union whose GPU member is one PCWSTR FileName; the buffer covers the whole union.
        var parameters = stackalloc nint[16];

        fixed (char* name = m_path) {
            parameters[0] = ((nint)name);

            var result = m_begin(parameters);

            return ((result < 0) ? $"BeginProgrammaticGpuCapture returned 0x{result:X8}" : null);
        }
    }
    /// <inheritdoc/>
    public GpuCaptureEnd End() {
        var result = m_end(0);

        return (result switch {
            Pending => GpuCaptureEnd.StillWriting(),
            < 0 => GpuCaptureEnd.Failed(reason: $"EndProgrammaticGpuCapture returned 0x{result:X8}"),
            _ => GpuCaptureEnd.Written(path: m_path!),
        });
    }
}
