using Puck.SdfVm;

namespace Puck.Testing;

/// <summary>Films the frame supplied by a test, until the test replaces it.</summary>
public sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
    public SdfFrame Frame { get; set; } = frame;

    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => Frame;
}
