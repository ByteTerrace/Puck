using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// A live hardware-camera viewport source (<c>$type: "live-camera"</c>) — the first non-SDF-camera
/// <see cref="ViewportSource"/> kind, and the payload that makes a webcam <em>authorable per viewport</em>,
/// interchangeable with an orbit/perspective camera at the same seam. Unlike a <see cref="CameraDocument"/> it builds no
/// virtual <c>ICamera</c>; the demo hosts a live-camera producer node for its slot instead (opening the platform capture
/// device, uploading each frame, and sampling it into the pane). The optional effect knobs let the SDF/effects sampling
/// stage be authored in the document too: <see cref="PixelSize"/> is the retro pixelation cell size and
/// <see cref="Quantize"/> the per-channel color-depth reduction (both off at their <c>0</c> defaults).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LiveCameraViewportSource : ViewportSource {
    /// <summary>The retro pixelation cell size in destination pixels applied when the camera is sampled into the pane; <c>0</c>/<c>1</c> disables it.</summary>
    public int PixelSize { get; init; }
    /// <summary>The per-channel color levels the sampled camera is quantized to (e.g. <c>8</c> for a 3-bit palette look); <c>0</c>/<c>1</c> disables it.</summary>
    public int Quantize { get; init; }

    internal override void Validate(string path, ValidationErrors errors) {
        if (PixelSize < 0) {
            errors.Add(path: $"{path}.pixelSize", message: "pixelSize must be zero or greater");
        }

        if (Quantize < 0) {
            errors.Add(path: $"{path}.quantize", message: "quantize must be zero or greater");
        }
    }
}
