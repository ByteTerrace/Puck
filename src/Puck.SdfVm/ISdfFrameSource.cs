namespace Puck.SdfVm;

public interface ISdfFrameSource {
    /// <summary>Captures the scene, cameras, and per-entity transforms for one rendered frame.</summary>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="deltaSeconds">The seconds the simulation advanced since the previous frame (for time-based presentation smoothing).</param>
    /// <param name="interpolationAlpha">The fraction in <c>[0, 1)</c> between the previous and current fixed simulation tick, for interpolating presentation state toward the variable display rate; a static source may ignore it.</param>
    /// <returns>The frame to render.</returns>
    SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha);
}
