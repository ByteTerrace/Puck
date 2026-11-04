using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>Finite route observations prove tick coverage independently of authority replay and timing. Their
/// content signatures use public semantic values, including motion cuts, rather than process object identities.</summary>
public sealed class WorldPresentationProbeLawTests {
    [Fact]
    public void TheUnarmedObserverDoesNoWorkAndEndRetainsTheLastWindow() {
        var probe = new WorldPresentationProbe();
        var frame = Frame();
        Assert.Null(probe.Observer);
        Assert.Null(probe.Read());
        Assert.Equal(0L, AllocationWindow.Least(() => Record(probe, 1, frame)));
        probe.Begin(1, 1);
        Assert.Same(probe, probe.Observer);
        Record(probe, 1, frame);
        probe.End();
        Assert.Null(probe.Observer);
        Record(probe, 1, frame);
        Assert.True(probe.Read()!.Complete);
    }

    [Fact]
    public void CoverageRefusesMissingDuplicateAndReorderedTicksWithoutOverwritingTheFirstSample() {
        var probe = new WorldPresentationProbe();
        var frame = Frame();
        probe.Begin(10, 13);
        Record(probe, 9, frame);
        Record(probe, 10, frame);
        Record(probe, 12, frame);
        Record(probe, 11, frame);
        Record(probe, 10, frame with { Time = 99f });
        var trace = probe.Read()!;
        Assert.False(trace.Complete);
        Assert.Equal(3, trace.SampleCount);
        Assert.Equal(new ulong[] { 13 }, trace.Missing);
        Assert.Equal(new ulong[] { 10 }, trace.DuplicateTicks);
        Assert.Equal(new ulong[] { 10, 11 }, trace.OutOfOrderTicks);
        Assert.Equal(BitConverter.SingleToUInt32Bits(frame.Time), trace.Samples[0].TimeBits);
        probe.Begin(10, 13);
        for (ulong tick = 10; tick <= 13; tick++) { Record(probe, tick, frame); }
        Assert.True(probe.Read()!.Complete);
    }

    [Fact]
    public void TimingExcludesWarmupAndJsonCarriesTheActualClockAndLightingPolicies() {
        var time = new ManualTime();
        var probe = new WorldPresentationProbe(time);
        var frame = Frame() with { Clock = new PresentedTick(7, .25), TickLocked = true, ConvergesLighting = false };
        probe.Begin(1, 3, 2);
        Record(probe, 1, frame);
        time.Timestamp = 100;
        Record(probe, 2, frame);
        time.Timestamp = 350;
        Record(probe, 3, frame);
        var trace = probe.Read()!;
        Assert.True(trace.Complete);
        Assert.Equal(250d, trace.ElapsedMilliseconds);
        using var json = JsonDocument.Parse(probe.ReadJson());
        var sample = json.RootElement.GetProperty("samples")[0];
        Assert.Equal(7UL, sample.GetProperty("clockWhole").GetUInt64());
        Assert.Equal("3fd0000000000000", sample.GetProperty("clockFractionBits").GetString());
        Assert.True(sample.GetProperty("tickLocked").GetBoolean());
        Assert.False(sample.GetProperty("convergesLighting").GetBoolean());
        Assert.Equal(640U, sample.GetProperty("width").GetUInt32());
    }

    [Fact]
    public void IndependentEqualProgramsAndAuthoredIdentitiesAgreeWhileRealInputsDiffer() {
        var first = Frame();
        var second = Frame();
        Assert.NotSame(first.Program, second.Program);
        Assert.Equal(Sample(first), Sample(second));
        Assert.NotEqual(Sample(first).Program, Sample(Frame(2f)).Program);
        var transform = new DynamicTransform(Vector3.Zero, Quaternion.Identity);
        var posed = first with { DynamicTransforms = new[] { transform } };
        Assert.NotEqual(Sample(posed).Transforms, Sample(posed with { DynamicTransforms = new[] { transform with { MaterialScale = 2f } } }).Transforms);
        Assert.NotEqual(Sample(posed).Transforms, Sample(posed with { DynamicTransforms = new[] { transform with { Scale = 2f } } }).Transforms);
        var negativeZero = BitConverter.UInt32BitsToSingle(0x80000000U);
        Assert.NotEqual(Sample(posed).Transforms, Sample(posed with { DynamicTransforms = new[] { transform with { Position = new Vector3(negativeZero, 0f, 0f) } } }).Transforms);
        Assert.NotEqual(Sample(first).Views, Sample(first with { Views = new[] { first.Views[0] with { CutRevision = 1 } } }).Views);
    }

    [Fact]
    public void EqualPosesStillExposeHistoryCutsAndUnknownIdentityCannotQualify() {
        var frame = Frame();
        var target = new WorldPickTarget(null, 3) { Prototype = "body", Occurrence = 2 };
        var address = new WorldEntityAddress("authority", 3, 1);
        var copy = new SdfMeshInstance(0, target, new DynamicTransform(Vector3.Zero, Quaternion.Identity), 1f, true) { MotionIdentity = (target, address, 1) };
        var before = Sample(frame with { MeshInstances = new[] { copy } });
        var after = Sample(frame with { MeshInstances = new[] { copy with { MotionIdentity = (target, address, 2) } } });
        Assert.NotEqual(before.Transforms, after.Transforms);
        Assert.Equal(0, before.UnsupportedIdentityCount);
        var probe = new WorldPresentationProbe();
        probe.Begin(1, 1);
        Record(probe, 1, frame with { MeshInstances = new[] { copy with { MotionIdentity = new object() } } });
        var trace = probe.Read()!;
        Assert.True(trace.Complete);
        Assert.Equal(1, trace.Samples[0].UnsupportedIdentityCount);
        Assert.Contains("System.Object", trace.UnsupportedIdentityTypes);
    }

    [Fact]
    public void BorrowedAuthoredMeshContentIsReadLiveRatherThanCachedByReference() {
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var mesh = new SdfMesh(positions, new uint[] { 0, 1, 2 });
        var frame = Frame() with { MeshDraws = new[] { new SdfMeshDraw(mesh, Matrix4x4.Identity, 0, "authored") } };
        var probe = new WorldPresentationProbe();
        probe.Begin(1, 2);
        Record(probe, 1, frame);
        positions[1] = Vector3.UnitZ;
        Record(probe, 2, frame);
        var samples = probe.Read()!.Samples;
        Assert.NotEqual(samples[0].Transforms, samples[1].Transforms);
    }

    [Fact]
    public void WindowsAreBoundedAndRefusalsLeaveThePreviousObservationIntact() {
        var probe = new WorldPresentationProbe();
        probe.Begin(ulong.MaxValue, ulong.MaxValue);
        Record(probe, ulong.MaxValue, Frame());
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Begin(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Begin(0, WorldPresentationProbe.MaximumSamples));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.Begin(1, 2, 3));
        Assert.True(probe.Read()!.Complete);
        Assert.Equal(ulong.MaxValue, probe.Read()!.FirstTick);
    }

    private static void Record(WorldPresentationProbe probe, ulong tick, SdfFrame frame) => probe.Record(tick, frame, 1f / 60f, 1f, "authority", "document", 640, 480);
    private static WorldPresentationSample Sample(SdfFrame frame) {
        var probe = new WorldPresentationProbe();
        probe.Begin(1, 1);
        Record(probe, 1, frame);
        return Assert.Single(probe.Read()!.Samples);
    }
    private static SdfFrame Frame(float radius = 1f) {
        var builder = new SdfProgramBuilder();
        builder.Sphere(radius, builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One)));
        return new SdfFrame(builder.Build(), false, new[] { new SdfViewSnapshot(CameraSnapshot.LookAt(new Vector3(0f, 0f, -5f), Vector3.Zero, 1f, 640, 480), new NormalizedRect(0f, 0f, 1f, 1f)) }, 0f);
    }
    private sealed class ManualTime : TimeProvider {
        public long Timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }
}
