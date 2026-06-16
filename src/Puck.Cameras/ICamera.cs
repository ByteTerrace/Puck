namespace Puck.Cameras;

public interface ICamera {
    CameraSnapshot Capture(uint viewportWidth, uint viewportHeight);
}
