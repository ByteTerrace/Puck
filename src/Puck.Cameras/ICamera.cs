namespace Puck.Cameras;

public interface ICamera {
    /// <summary>Advances any time-varying animation this camera carries (a no-op for a static camera).</summary>
    /// <param name="deltaSeconds">Seconds elapsed since the previous frame.</param>
    void Advance(float deltaSeconds);
    /// <summary>Captures the camera's basis and projection for a viewport of the given pixel extent.</summary>
    /// <param name="viewportWidth">The viewport width in pixels.</param>
    /// <param name="viewportHeight">The viewport height in pixels.</param>
    /// <returns>The immutable snapshot the renderer reads.</returns>
    CameraSnapshot Capture(uint viewportWidth, uint viewportHeight);
}
