namespace Puck.Demo.Cameras;

/// <summary>A first-class camera: anything that can resolve itself to a <see cref="CameraSnapshot"/> for
/// a given viewport size. Orbit, free-fly, and the future follow cameras (Lakitu/lure) are all just
/// different motion policies behind this one surface.</summary>
internal interface ICamera {
    CameraSnapshot Capture(uint viewportWidth, uint viewportHeight);
}
