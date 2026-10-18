using System.Runtime.InteropServices;
using System.Text;
using Puck.Abstractions.Gpu;

namespace Puck.Launcher;

/// <summary>
/// RenderDoc's in-application API as a <see cref="IGpuCaptureTool"/>: <c>renderdoc.dll</c> (or <c>librenderdoc.so</c>)
/// loaded into the process before any graphics device or instance exists, so its Direct3D 12 hooks and its Vulkan layer
/// see the device from its creation. It captures one frame at a time between <c>StartFrameCapture</c> and
/// <c>EndFrameCapture</c> on whichever device is active, names each file from the path template with RenderDoc's own
/// <c>_frame&lt;n&gt;.rdc</c> suffix, and its overlay is turned off so captures and screenshots carry none.
/// </summary>
public sealed unsafe class RenderDocCaptureTool : IGpuCaptureTool {
    /// <summary><c>eRENDERDOC_API_Version_1_6_0</c>, the API version requested.</summary>
    public const int ApiVersion = 10600;

    private readonly RenderDocApi* m_api;

    private RenderDocCaptureTool(RenderDocApi* api) {
        m_api = api;
        // MaskOverlayBits(and: 0, or: eRENDERDOC_Overlay_None): no overlay on any presented frame.
        api->MaskOverlayBits(0U, 0U);
    }

    /// <inheritdoc/>
    public bool CapturesSpanFrames => false;
    /// <inheritdoc/>
    public string Name => "renderdoc";

    /// <summary>Loads RenderDoc into the process and binds its API, before any graphics device or instance is
    /// created.</summary>
    /// <param name="tool">The loaded tool, or <see langword="null"/> when it could not be loaded.</param>
    /// <param name="failure">Why it could not be loaded, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the tool loaded.</returns>
    public static bool TryLoad(out RenderDocCaptureTool? tool, out string? failure) {
        tool = null;

        var path = (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(folder: Environment.SpecialFolder.ProgramFiles), "RenderDoc", "renderdoc.dll")
            : "librenderdoc.so");

        if (!NativeLibrary.TryLoad(libraryPath: path, handle: out var library)) {
            failure = $"RenderDoc is not installed ({path} did not load)";

            return false;
        }

        if (!NativeLibrary.TryGetExport(handle: library, name: "RENDERDOC_GetAPI", address: out var getApi)) {
            failure = $"{path} exports no RENDERDOC_GetAPI";

            return false;
        }

        RenderDocApi* api = null;

        if (0 == ((delegate* unmanaged[Cdecl]<int, RenderDocApi**, int>)getApi)(ApiVersion, &api) || (api is null)) {
            failure = $"{path} does not offer API version 1.6.0";

            return false;
        }

        tool = new RenderDocCaptureTool(api: api);
        failure = null;

        return true;
    }

    /// <inheritdoc/>
    public string? Start(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var template = Encoding.UTF8.GetBytes(s: $"{Path.ChangeExtension(path: path, extension: null)}\0");

        fixed (byte* text = template) {
            m_api->SetCaptureFilePathTemplate(text);
        }

        m_api->StartFrameCapture(0, 0);

        return null;
    }
    /// <inheritdoc/>
    public GpuCaptureEnd End() {
        if (0U == m_api->EndFrameCapture(0, 0)) {
            return GpuCaptureEnd.Failed(reason: "EndFrameCapture captured nothing (no frame was in progress on a device RenderDoc hooked)");
        }

        var index = (m_api->GetNumCaptures() - 1U);
        uint length = 0;

        if (0U == m_api->GetCapture(index, null, &length, null)) {
            return GpuCaptureEnd.Failed(reason: "RenderDoc reports no capture written");
        }

        var name = new byte[length];

        fixed (byte* text = name) {
            _ = m_api->GetCapture(index, text, &length, null);
        }

        return GpuCaptureEnd.Written(path: Encoding.UTF8.GetString(bytes: name).TrimEnd(trimChar: '\0'));
    }

    // RENDERDOC_API_1_6_0 (renderdoc_app.h): the function table RENDERDOC_GetAPI fills, in declaration order.
    [StructLayout(LayoutKind.Sequential)]
    private struct RenderDocApi {
        public nint GetApiVersion;
        public nint SetCaptureOptionU32;
        public nint SetCaptureOptionF32;
        public nint GetCaptureOptionU32;
        public nint GetCaptureOptionF32;
        public nint SetFocusToggleKeys;
        public nint SetCaptureKeys;
        public nint GetOverlayBits;
        public delegate* unmanaged[Cdecl]<uint, uint, void> MaskOverlayBits;
        public nint RemoveHooks;
        public nint UnloadCrashHandler;
        public delegate* unmanaged[Cdecl]<byte*, void> SetCaptureFilePathTemplate;
        public nint GetCaptureFilePathTemplate;
        public delegate* unmanaged[Cdecl]<uint> GetNumCaptures;
        public delegate* unmanaged[Cdecl]<uint, byte*, uint*, ulong*, uint> GetCapture;
        public nint TriggerCapture;
        public nint IsTargetControlConnected;
        public nint LaunchReplayUi;
        public nint SetActiveWindow;
        public delegate* unmanaged[Cdecl]<nint, nint, void> StartFrameCapture;
        public nint IsFrameCapturing;
        public delegate* unmanaged[Cdecl]<nint, nint, uint> EndFrameCapture;
        public nint TriggerMultiFrameCapture;
        public nint SetCaptureFileComments;
        public nint DiscardFrameCapture;
        public nint ShowReplayUi;
        public nint SetCaptureTitle;
    }
}
