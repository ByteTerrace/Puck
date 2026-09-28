using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: an instance declared camera-hidden (<see cref="SdfInstanceRange.CameraHidden"/>) packs
/// <see cref="SdfProgram.CameraHiddenInstanceFlag"/> into its meta's segmentEnd lane beside its segment range, which
/// <see cref="SdfProgram.SegmentEndMask"/> recovers unchanged, and an ordinary instance packs no flag.
/// </summary>
public sealed class InstanceCameraHiddenLawTests {
    // The instance directory's first word, found as the kernels find it: the segment directory's vector (past the
    // materials and the instruction table), then its header and two vectors per segment.
    private static int InstanceDirectoryWord(ReadOnlySpan<uint> words) {
        var segmentVector = checked((int)((words[3] + (20u * words[1])) + (2u * words[0])));

        return checked((((segmentVector + 1) + (2 * ((int)words[(segmentVector * 4)]))) * 4));
    }

    [Fact]
    public void ACameraHiddenInstancePacksItsFlagBesideItsSegmentRange() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        foreach (var hidden in ((bool[])[false, true])) {
            var center = new Vector3(
                x: (hidden ? 4f : 0f),
                y: 0f,
                z: 0f
            );

            _ = builder.BeginInstance(
                boundCenter: center,
                boundRadius: 1f,
                cameraHidden: hidden
            );
            _ = SdfSolidGeometry.AppendPrimitive(
                chain: builder.ResetPoint().Translate(offset: center),
                material: material,
                type: SdfSolidPrimitive.Sphere
            );
            _ = builder.EndInstance();
        }

        var program = builder.Build();
        var words = program.Words.ToArray();
        var directory = InstanceDirectoryWord(words: words);
        // Each instance is two vectors after the directory header: its bound, then its meta, whose w lane is segmentEnd.
        uint SegmentEndLane(int instance) => words[((directory + (((1 + (2 * instance)) + 1) * 4)) + 3)];

        Assert.Equal(expected: [false, true], actual: program.Instances.Select(selector: static instance => instance.CameraHidden));
        Assert.Equal(expected: 2u, actual: words[directory]);
        Assert.Equal(expected: 0u, actual: SegmentEndLane(instance: 0) & ~SdfProgram.SegmentEndMask);
        Assert.Equal(expected: SdfProgram.CameraHiddenInstanceFlag, actual: SegmentEndLane(instance: 1) & ~SdfProgram.SegmentEndMask);
        Assert.True(condition: ((SegmentEndLane(instance: 1) & SdfProgram.SegmentEndMask) > (SegmentEndLane(instance: 0) & SdfProgram.SegmentEndMask)));
    }
}
