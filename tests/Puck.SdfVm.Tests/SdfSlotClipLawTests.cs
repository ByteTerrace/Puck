using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a body straddling a portal is drawn on both sides of it. Every draw whose frame slot lies in a straddling
/// run is clipped to the glass it straddles and gains a copy, moved through the pair's rigid motion and clipped to the
/// counterpart's glass, whose identity is stable and never the original's; a draw outside every run is untouched. The
/// region record carries each draw's carry and clip plane at the words the kernels read them from.
/// </summary>
public sealed class SdfSlotClipLawTests {
    private static readonly SdfMesh Triangle = new(
        indices: new uint[] { 0, 1, 2 },
        positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }
    );
    private static readonly SdfSlotClip Straddle = new(
        CopyPlane: new Vector4(w: 6f, x: 0f, y: -1f, z: 0f),
        CopyRotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: MathF.PI),
        CopyTranslation: new Vector3(x: 0f, y: 6f, z: 0f),
        FirstSlot: 10,
        Plane: new Vector4(w: 0f, x: 0f, y: 1f, z: 0f),
        SlotCount: 4
    );

    private static SdfMeshDraw Draw(string identity, int? slot) => new(
        Identity: identity,
        Material: 0,
        Mesh: Triangle,
        ObjectToWorld: Matrix4x4.Identity
    ) { FrameSlot = slot };

    [Fact]
    public void ADrawInAStraddlingRunIsClippedAndCopiedThroughThePair() {
        var draws = new List<SdfMeshDraw> { Draw(identity: "body", slot: 12), Draw(identity: "wall", slot: 3), Draw(identity: "ground", slot: null) };

        SdfSlotClip.Apply(
            clips: [Straddle],
            draws: draws
        );

        Assert.Equal(expected: 4, actual: draws.Count);
        Assert.Equal(expected: Straddle.Plane, actual: draws[0].ClipPlane);
        Assert.Equal(expected: Quaternion.Identity, actual: draws[0].CarryRotation);
        Assert.Equal(expected: default, actual: draws[1].ClipPlane);
        Assert.Equal(expected: default, actual: draws[2].ClipPlane);

        var copy = draws[3];

        Assert.Equal(expected: (Straddle.CopyRotation, Straddle.CopyTranslation, Straddle.CopyPlane), actual: (copy.CarryRotation, copy.CarryTranslation, copy.ClipPlane));
        Assert.Equal(expected: 12, actual: copy.FrameSlot);
        Assert.NotEqual(expected: draws[0].Identity, actual: copy.Identity);

        var again = new List<SdfMeshDraw> { Draw(identity: "body", slot: 12) };

        SdfSlotClip.Apply(clips: [Straddle], draws: again);
        Assert.Equal(expected: copy.Identity, actual: again[1].Identity);
    }
    // The motion history copies each draw's matrix words at its own index (SdfWorldTables.CopyMeshHistory) and keeps it
    // while the identity at that index holds (StageMeshMotion); the kernels carry the previous matrix by the draw's own
    // carry (sdfMeshPreviousPosition). A copy therefore reprojects through the carried previous pose only if it holds
    // the original's matrix, its own carry, and the same identity at the same index frame after frame.
    [Fact]
    public void ACopyKeepsItsOwnHistoryTheOriginalsMatrixUnderItsOwnCarry() {
        List<SdfMeshDraw> Frame() {
            var draws = new List<SdfMeshDraw> { Draw(identity: "wall", slot: 3), Draw(identity: "body", slot: 12) };

            SdfSlotClip.Apply(clips: [Straddle], draws: draws);

            return draws;
        }

        var first = Frame();
        var second = Frame();

        Assert.Equal(expected: first.Select(selector: static draw => draw.Identity), actual: second.Select(selector: static draw => draw.Identity));

        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);
        var layout = SdfMeshRegion.Plan(draws: first, meshes: placements);
        var words = new uint[layout.Words];

        SdfMeshRegion.Write(destination: words, draws: first, layout: layout, meshes: placements);

        var original = words.AsSpan(length: SdfMeshRecord.DrawWords, start: SdfMeshRecord.DrawWords);
        var copy = words.AsSpan(length: SdfMeshRecord.DrawWords, start: (2 * SdfMeshRecord.DrawWords));

        Assert.True(condition: copy[..16].SequenceEqual(other: original[..16]));
        Assert.False(condition: copy.Slice(length: 7, start: SdfMeshRecord.CarryRotationWord).SequenceEqual(other: original.Slice(length: 7, start: SdfMeshRecord.CarryRotationWord)));
    }
    [Fact]
    public void TheRecordCarriesTheCarryAndTheClipPlaneWhereTheKernelsReadThem() {
        var draw = (Draw(identity: "body", slot: 12) with {
            CarryRotation = Straddle.CopyRotation,
            CarryTranslation = Straddle.CopyTranslation,
            ClipPlane = Straddle.CopyPlane,
        });
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);
        var layout = SdfMeshRegion.Plan(draws: [draw], meshes: placements);
        var words = new uint[layout.Words];

        SdfMeshRegion.Write(destination: words, draws: [draw], layout: layout, meshes: placements);

        float Word(int index) => BitConverter.UInt32BitsToSingle(value: words[index]);

        Assert.Equal(expected: Straddle.CopyRotation, actual: new Quaternion(x: Word(index: SdfMeshRecord.CarryRotationWord), y: Word(index: (SdfMeshRecord.CarryRotationWord + 1)), z: Word(index: (SdfMeshRecord.CarryRotationWord + 2)), w: Word(index: (SdfMeshRecord.CarryRotationWord + 3))));
        Assert.Equal(expected: Straddle.CopyTranslation, actual: new Vector3(x: Word(index: SdfMeshRecord.CarryTranslationWord), y: Word(index: (SdfMeshRecord.CarryTranslationWord + 1)), z: Word(index: (SdfMeshRecord.CarryTranslationWord + 2))));
        Assert.Equal(expected: Straddle.CopyPlane, actual: new Vector4(x: Word(index: SdfMeshRecord.ClipPlaneWord), y: Word(index: (SdfMeshRecord.ClipPlaneWord + 1)), z: Word(index: (SdfMeshRecord.ClipPlaneWord + 2)), w: Word(index: (SdfMeshRecord.ClipPlaneWord + 3))));
        Assert.Equal(actual: (SdfMeshRecord.ClipPlaneWord + 4), expected: SdfMeshRecord.DrawWords);
    }
}
