using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Records PIX debug-marker events on a Direct3D 12 command list, the peer of Vulkan's
/// <c>vkCmdBeginDebugUtilsLabelEXT</c> labels, which GPU capture tools (PIX, RenderDoc, Nsight) show as a labeled scope.
/// Each event is a PIX3 blob (<c>WINPIX_EVENT_PIX3BLOB_VERSION</c>), the encoding <c>PIXBeginEvent</c> writes on a
/// command list, so PIX reads the label as it is rather than flagging a legacy string event. With no capture attached
/// the driver treats <c>BeginEvent</c>/<c>EndEvent</c> as no-ops, so it is safe on every frame and never affects
/// rendered output.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXDebugLabel {
    // The blob's layout is pix3's (PIXEventsCommon.h in https://github.com/microsoft/PixEvents): an event-info word
    // (PIXEncodeEventInfo with a zero timestamp and PIXEvent_BeginEvent_NoArgs), a color word, a string-info word
    // (PIXEncodeStringInfo: no alignment, 8-byte copy chunks, wide, not a shortcut), then the UTF-16 name four characters
    // a word, low character first, ending in the word that holds its terminating zero.
    private const uint Pix3BlobVersion = 2;
    private const ulong BeginEventNoArgs = 0x002UL;
    private const int EventTypeShift = 10;
    private const ulong StringCopyChunkBytes = 8UL;
    private const int StringCopyChunkShift = 55;
    // PIX_COLOR_INDEX(0): an index into PIX's own palette rather than an RGB color.
    private const ulong LabelColor = 0UL;

    /// <summary>Opens a labeled scope on a command list, closed by <see cref="End"/>.</summary>
    /// <param name="commandList">The command list to record on.</param>
    /// <param name="label">The scope's name.</param>
    public static void Begin(ID3D12GraphicsCommandList* commandList, string label) {
        // Three header words, then one word per four characters with the terminating zero in the last.
        var words = (3 + ((label.Length / 4) + 1));
        var blob = ((words <= 64) ? stackalloc ulong[words] : new ulong[words]);

        blob[0] = (BeginEventNoArgs << EventTypeShift);
        blob[1] = LabelColor;
        blob[2] = (StringCopyChunkBytes << StringCopyChunkShift);
        blob[3..].Clear();

        for (var index = 0; (index < label.Length); index++) {
            blob[(3 + (index / 4))] |= (((ulong)label[index]) << (16 * (index % 4)));
        }

        fixed (ulong* data = blob) {
            commandList->BeginEvent(
                Metadata: Pix3BlobVersion,
                pData: data,
                Size: ((uint)(words * sizeof(ulong)))
            );
        }
    }
    /// <summary>Closes the scope the latest <see cref="Begin"/> opened.</summary>
    /// <param name="commandList">The command list to record on.</param>
    public static void End(ID3D12GraphicsCommandList* commandList) {
        commandList->EndEvent();
    }
}
