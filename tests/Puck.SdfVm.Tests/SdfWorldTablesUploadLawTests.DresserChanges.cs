using System.Collections;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [Fact]
    public void DressedChangesKeepCapturedEndpointsAndCoverBothRootsWhenAConsumerSkipsAFrame() {
        using var producer = new RootCutProducer(skinned: false, secondGroup: true);
        var first = producer.Publish(1, 1, 1);
        var second = producer.Publish(2, 1, 1);
        var secondDraws = second.MeshDraws.ToArray();
        var third = producer.Publish(2, 3, 1);
        var changed = new GpuUploadRuns(capacity: 8);

        Assert.True(third.MeshDrawChanges!.TryCollect(first.MeshDrawChangesSerial, second.MeshDrawChangesSerial, changed));
        Assert.Equal(new[] { 0 }, DresserRows(changed));
        Assert.Equal(Matrix4x4.CreateTranslation(2, 0, 0), secondDraws[0].ObjectToWorld);
        Assert.Equal(Matrix4x4.CreateTranslation(0, 1, 0), secondDraws[1].ObjectToWorld);
        Assert.True(third.MeshDrawChanges.TryCollect(first.MeshDrawChangesSerial, third.MeshDrawChangesSerial, changed));
        Assert.Equal(new[] { 0, 1 }, DresserRows(changed));
        Assert.Equal(Matrix4x4.CreateTranslation(2, 0, 0), third.MeshDraws[0].ObjectToWorld);
        Assert.Equal(Matrix4x4.CreateTranslation(0, 3, 0), third.MeshDraws[1].ObjectToWorld);
    }

    [Fact]
    public void AHostRevisionWithoutFeedAdvanceStillDressesItsChangedRecord() {
        using var producer = new RootCutProducer(skinned: false);
        var draws = new List<SdfMeshDraw> { HostDraw("host", 1) };
        var feed = new GpuChangedRows(history: 4);
        feed.Begin(everything: true, rows: draws.Count);
        var first = producer.Dress(producer.FrameAt(1, 1, 1) with { MeshDraws = draws, MeshDrawsRevision = 1, MeshDrawChanges = feed, MeshDrawChangesSerial = feed.Serial });
        draws[0] = draws[0] with { ObjectToWorld = Matrix4x4.CreateTranslation(7, 0, 0) };
        var next = producer.Dress(producer.FrameAt(1, 1, 1) with { MeshDraws = draws, MeshDrawsRevision = 2, MeshDrawChanges = feed, MeshDrawChangesSerial = feed.Serial });
        var changed = new GpuUploadRuns(capacity: 8);

        Assert.Equal(Matrix4x4.CreateTranslation(7, 0, 0), next.MeshDraws[0].ObjectToWorld);
        Assert.True(next.MeshDrawChanges!.TryCollect(first.MeshDrawChangesSerial, next.MeshDrawChangesSerial, changed));
        Assert.Equal(new[] { 0 }, DresserRows(changed));
    }

    [Fact]
    public void RemovingAClipRestoresTheBaseRecordAndPublishesACompleteRosterRefresh() {
        using var producer = new RootCutProducer(skinned: false);
        var clip = new SdfSlotClip(0, 1, new Vector4(1, 0, 0, 0), Quaternion.Identity, new Vector3(10, 0, 0), new Vector4(-1, 0, 0, -10));
        var clipped = producer.Dress(producer.FrameAt(1, 1, 1) with { SlotClips = [clip] });
        var baseIdentity = clipped.MeshDraws[0].Identity;
        Assert.Equal(2, clipped.MeshDraws.Count);
        var restored = producer.Publish(1, 1, 1);
        var draw = Assert.Single(restored.MeshDraws);

        Assert.Equal(default, draw.ClipPlane);
        Assert.Equal(Vector3.Zero, draw.CarryTranslation);
        Assert.Equal(Quaternion.Identity, draw.CarryRotation);
        Assert.Equal(baseIdentity, draw.Identity);
        Assert.False(restored.MeshDrawChanges!.TryCollect(clipped.MeshDrawChangesSerial, restored.MeshDrawChangesSerial, new GpuUploadRuns(capacity: 8)));
    }

    [Fact]
    public void AFailedPublicationRetriesFromCanonicalInputsWithoutAdvancingItsJournal() {
        using var producer = new RootCutProducer(skinned: false);
        SdfMeshDraw[] original = [HostDraw("a", 1), HostDraw("b", 2)];
        var first = producer.Dress(producer.FrameAt(1, 1, 1) with { MeshDraws = original, MeshDrawsRevision = 1 });
        var next = new ThrowOnceDraws([original[0] with { ObjectToWorld = Matrix4x4.CreateTranslation(7, 0, 0) }, original[1] with { ObjectToWorld = Matrix4x4.CreateTranslation(8, 0, 0) }]);
        var input = producer.FrameAt(1, 1, 1) with { MeshDraws = next, MeshDrawsRevision = 2 };

        Assert.Throws<InvalidOperationException>(() => producer.Dress(input));
        Assert.Equal(first.MeshDrawChangesSerial, first.MeshDrawChanges!.Serial);
        var retried = producer.Dress(input);
        Assert.Equal(first.MeshDrawChangesSerial + 1, retried.MeshDrawChangesSerial);
        Assert.Equal(Matrix4x4.CreateTranslation(7, 0, 0), retried.MeshDraws[0].ObjectToWorld);
        Assert.Equal(Matrix4x4.CreateTranslation(8, 0, 0), retried.MeshDraws[1].ObjectToWorld);
        Assert.Equal(Matrix4x4.CreateTranslation(1, 0, 0), retried.MeshDraws[2].ObjectToWorld);
        Assert.False(retried.MeshDrawChanges!.TryCollect(first.MeshDrawChangesSerial, retried.MeshDrawChangesSerial, new GpuUploadRuns(capacity: 8)));
    }

    private static SdfMeshDraw HostDraw(string identity, float x) => new(
        Mesh: new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 }),
        ObjectToWorld: Matrix4x4.CreateTranslation(x, 0, 0), Material: 0, Identity: identity);

    private static int[] DresserRows(GpuUploadRuns runs) => Enumerable.Range(0, runs.Count).SelectMany(index => Enumerable.Range(runs.Start(index), runs.Length(index))).ToArray();

    private sealed class ThrowOnceDraws(SdfMeshDraw[] draws) : IReadOnlyList<SdfMeshDraw> {
        private bool m_throws = true;
        public int Count => draws.Length;
        public SdfMeshDraw this[int index] {
            get {
                if (m_throws && index == 1) { m_throws = false; throw new InvalidOperationException("publication fault after the first host record"); }
                return draws[index];
            }
        }
        public IEnumerator<SdfMeshDraw> GetEnumerator() { for (var index = 0; index < draws.Length; index++) { yield return this[index]; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
