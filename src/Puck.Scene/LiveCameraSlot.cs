namespace Puck.Scene;

/// <summary>
/// A resolved live-camera viewport: the viewport index a hardware camera fills, plus the effect knobs authored on its
/// <see cref="LiveCameraViewportSource"/>. <see cref="ViewportBuilder"/> emits one per <c>live-camera</c> viewport so the
/// host can build a producer node for the slot without re-walking the document.
/// </summary>
/// <param name="Slot">The viewport index (into the parallel camera/region arrays) the live camera fills.</param>
/// <param name="PixelSize">The retro pixelation cell size to sample the camera with (<c>&lt;= 1</c> disables it).</param>
/// <param name="Quantize">The per-channel color levels to quantize the sampled camera to (<c>&lt;= 1</c> disables it).</param>
public readonly record struct LiveCameraSlot(int Slot, int PixelSize, int Quantize);
