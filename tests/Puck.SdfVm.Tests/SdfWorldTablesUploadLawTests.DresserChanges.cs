using System.Collections;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [Fact]
    public void DressedRowsOfMovingGroupsStandWhileTheirSlotsMoveAndPlaceWhereTheRowsStand() {
        using var producer = new RootCutProducer(secondGroup: true, skinned: false);
        var first = producer.Publish(1, 1, 1);
        var firstEnd = Endpoint(frame: first);
        var second = producer.Publish(2, 1, 1);
        var third = producer.Publish(2, 3, 1);
        var changed = new GpuUploadRuns(capacity: 8);

        Assert.True(condition: DresserWindow(first: firstEnd, into: changed, through: Endpoint(frame: third)));
        Assert.Empty(collection: DresserRows(runs: changed));
        Assert.Equal(Matrix4x4.CreateTranslation(xPosition: 2, yPosition: 0, zPosition: 0), second.DressedDraws()[0].PlacementIn(transforms: second.DynamicTransforms));
        Assert.Equal(Matrix4x4.CreateTranslation(xPosition: 0, yPosition: 3, zPosition: 0), third.DressedDraws()[1].PlacementIn(transforms: third.DynamicTransforms));
    }
    [Fact]
    public void AHostRevisionWithoutFeedAdvanceStillDressesItsChangedRecord() {
        using var producer = new RootCutProducer(skinned: false);
        var draws = new List<SdfMeshDraw> { HostDraw(identity: "host", x: 1) };
        var feed = new GpuChangedRows(history: 4);

        feed.Begin(everything: true, rows: draws.Count);
        var first = producer.Dress(frame: producer.FrameAt(1, 1, 1) with { MeshDraws = draws, MeshDrawsRevision = 1, MeshDrawChanges = feed.Reader, MeshDrawChangesSerial = feed.Serial });
        var firstEnd = Endpoint(frame: first);

        draws[0] = draws[0].PlacedAt(objectToWorld: Matrix4x4.CreateTranslation(xPosition: 7, yPosition: 0, zPosition: 0));
        var next = producer.Dress(frame: producer.FrameAt(1, 1, 1) with { MeshDraws = draws, MeshDrawsRevision = 2, MeshDrawChanges = feed.Reader, MeshDrawChangesSerial = feed.Serial });
        var changed = new GpuUploadRuns(capacity: 8);

        Assert.Equal(Matrix4x4.CreateTranslation(xPosition: 7, yPosition: 0, zPosition: 0), next.DressedDraws()[0].FixedPlacement);
        Assert.True(condition: DresserWindow(first: firstEnd, into: changed, through: Endpoint(frame: next)));
        Assert.Equal(new[] { 0 }, DresserRows(runs: changed));
    }
    [Fact]
    public void RemovingAClipRestoresTheBaseRecordAndJournalsItsRow() {
        using var producer = new RootCutProducer(skinned: false);
        var clip = new SdfSlotClip(0, 1, new Vector4(w: 0, x: 1, y: 0, z: 0), Quaternion.Identity, new Vector3(x: 10, y: 0, z: 0), new Vector4(w: -10, x: -1, y: 0, z: 0));
        var clipped = producer.Dress(frame: producer.FrameAt(1, 1, 1) with { SlotClips = [clip] });
        var baseIdentity = clipped.DressedDraws()[0].Identity;
        var clippedEnd = Endpoint(frame: clipped);

        Assert.Equal(2, clipped.DressedDraws().Count);
        var restored = producer.Publish(1, 1, 1);
        var draw = Assert.Single(collection: restored.DressedDraws());

        Assert.Equal(default, draw.ClipPlane);
        Assert.Equal(Vector3.Zero, draw.CarryTranslation);
        Assert.Equal(Quaternion.Identity, draw.CarryRotation);
        Assert.Equal(baseIdentity, draw.Identity);
        Assert.True(condition: DresserWindow(clippedEnd, Endpoint(frame: restored), new GpuUploadRuns(8)));
    }
    [Fact]
    public void AFailedPublicationRetriesFromCanonicalInputsWithoutAdvancingItsJournal() {
        using var producer = new RootCutProducer(skinned: false);
        SdfMeshDraw[] original = [HostDraw(identity: "a", x: 1), HostDraw(identity: "b", x: 2)];
        var first = producer.Dress(frame: producer.FrameAt(1, 1, 1) with { MeshDraws = original, MeshDrawsRevision = 1 });
        var firstEnd = Endpoint(frame: first);
        var next = new ThrowOnceDraws(draws: [original[0].PlacedAt(objectToWorld: Matrix4x4.CreateTranslation(xPosition: 7, yPosition: 0, zPosition: 0)), original[1].PlacedAt(objectToWorld: Matrix4x4.CreateTranslation(xPosition: 8, yPosition: 0, zPosition: 0))]);
        var input = producer.FrameAt(1, 1, 1) with { MeshDraws = next, MeshDrawsRevision = 2 };

        Assert.Throws<InvalidOperationException>(testCode: () => producer.Dress(frame: input));
        var retried = producer.Dress(frame: input);

        Assert.Equal((firstEnd.Serial + 1), retried.MeshRows!.Serial);
        Assert.Equal(Matrix4x4.CreateTranslation(xPosition: 7, yPosition: 0, zPosition: 0), retried.DressedDraws()[0].FixedPlacement);
        Assert.Equal(Matrix4x4.CreateTranslation(xPosition: 8, yPosition: 0, zPosition: 0), retried.DressedDraws()[1].FixedPlacement);
        Assert.Equal(Matrix4x4.CreateTranslation(xPosition: 1, yPosition: 0, zPosition: 0), retried.DressedDraws()[2].PlacementIn(transforms: retried.DynamicTransforms));
        Assert.False(condition: DresserWindow(firstEnd, Endpoint(frame: retried), new GpuUploadRuns(8)));
    }

    internal static SdfMeshDraw HostDraw(string identity, float x) => new(
        mesh: new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 }),
        objectToWorld: Matrix4x4.CreateTranslation(xPosition: x, yPosition: 0, zPosition: 0), material: 0, identity: identity);

    private static int[] DresserRows(GpuUploadRuns runs) => Enumerable.Range(0, runs.Count).SelectMany(selector: index => Enumerable.Range(runs.Start(index: index), runs.Length(index: index))).ToArray();
    // The dresser's rows and journal as one publication left them: a dressed frame reads the rows' live endpoint, so a
    // window between two publications holds each one's endpoint as it was.
    private readonly record struct DresserEnd(SdfMeshRows Rows, GpuRowChanges Changes, long Serial, long Revision, int Count);
    private static DresserEnd Endpoint(SdfFrame frame) => new(frame.MeshRows!, frame.MeshRows!.Changes, frame.MeshRows.Serial, frame.MeshRows.Revision, frame.MeshRows.Count);
    private static bool DresserWindow(DresserEnd first, DresserEnd through, GpuUploadRuns into) {
        var cursor = new GpuRowCursor();

        _ = cursor.Collect(first.Changes, first.Serial, first.Rows, first.Count, first.Revision, into);
        cursor.Commit();
        return !cursor.Collect(through.Changes, through.Serial, through.Rows, through.Count, through.Revision, into);
    }

    internal sealed class ThrowOnceDraws(SdfMeshDraw[] draws) : IReadOnlyList<SdfMeshDraw> {
        private bool m_throws = true;

        public int Count => draws.Length;

        public SdfMeshDraw this[int index] {
            get {
                if (m_throws && (index == 1)) { m_throws = false; throw new InvalidOperationException(message: "publication fault after the first host record"); }
                return draws[index];
            }
        }

        public IEnumerator<SdfMeshDraw> GetEnumerator() { for (var index = 0; (index < draws.Length); index++) { yield return this[index]; } }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
