namespace Puck.SdfVm;

public interface ISdfFrameSource {
    SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds);
}
