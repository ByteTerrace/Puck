using Puck.Cameras;
using Puck.Compositing;

namespace Puck.SdfVm;

public readonly record struct SdfViewSnapshot(CameraSnapshot Camera, NormalizedRect Region);
public sealed record SdfFrame(
    SdfProgram Program,
    bool ProgramChanged,
    IReadOnlyList<SdfViewSnapshot> Views,
    float Time,
    float WarpAmount
);
