using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Records PIX-compatible debug-marker events on a Direct3D 12 command list — the peer of Vulkan's
/// <c>vkCmdBeginDebugUtilsLabelEXT</c> labels. GPU capture tools (PIX / RenderDoc / Nsight) render them as a labeled
/// scope. The legacy string encoding (<c>PIX_EVENT_UNICODE_VERSION</c> plus a null-terminated UTF-16 name) is
/// understood by every tool; with no capture attached the driver treats <c>BeginEvent</c>/<c>EndEvent</c> as cheap
/// no-ops, so it is safe on every frame and never affects rendered output.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXDebugLabel {
    // PIX_EVENT_UNICODE_VERSION: pData is a null-terminated UTF-16 string, Size its byte length INCLUDING the null.
    private const uint PixEventUnicodeVersion = 2;

    public static void Begin(ID3D12GraphicsCommandList* commandList, string label) {
        // .NET strings are internally null-terminated, so the pinned pointer + (Length + 1) chars covers the null.
        fixed (char* name = label) {
            commandList->BeginEvent(
                Metadata: PixEventUnicodeVersion,
                pData: name,
                Size: (uint)((label.Length + 1) * sizeof(char))
            );
        }
    }
    public static void End(ID3D12GraphicsCommandList* commandList) {
        commandList->EndEvent();
    }
}
