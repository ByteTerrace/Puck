using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldIndirectReferenceLawTests {
    [Fact]
    public void ANearPickUsesItsCapturedRayAndRefusesMissingOrForeignSource() {
        var builder = new SdfProgramBuilder();
        var floor = builder.AddMaterial(new SdfMaterial(Vector3.Zero));
        var emitter = builder.AddMaterial(new SdfMaterial(Vector3.UnitX, Emissive: 1f, Metal: 1f));
        builder.Plane(Vector3.UnitY, 0f, floor);
        builder.ResetPoint().Translate(new Vector3(0f, .18f, 0f)).Sphere(.025f, emitter);
        var source = new SdfFrame(Program: builder.Build(), ProgramChanged: false, Time: 0f,
            Views: [new SdfViewSnapshot(CameraSnapshot.LookAt(fieldOfViewRadians: 1f,
                position: new Vector3(0f, .1f, -1f), target: new Vector3(0f, .1f, 0f),
                viewportHeight: 16, viewportWidth: 16), new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))]) {
            FarDistance = 1f,
            IndirectTier = SdfIndirectTier.High,
            IndirectSources = SdfIndirectSources.Emission,
            IndirectGains = SdfIndirectGains.One with { Emission = .25f },
        };
        var gpu = new FakeGpuDevice();
        var context = new FrameContext(AccumulatorTicks: 0UL, DeltaTicks: 0UL, ElapsedTicks: 0UL, FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0UL, TargetHeight: 16, TargetWidth: 16);
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(() => source), height: 16, kernels: SdfTestPipelines.Kernels(),
            name: "near-reference", pipelines: SdfTestPipelines.Cache(), width: 16);
        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var cache = residency.Tables!.Indirect!;
        void Complete() {
            TestLiveness.Within(frames: 64, step: () => {
                residency.BeginFrame();
                Assert.True(residency.Prepare(context));
                _ = residency.Submit(context);
                // Commit the admitted host work to obtain its actual immutable source. The fake device provides
                // no radiance answer; the red incoming value below follows independently from the emitter.
                cache.Submitted();
                cache.SubmittedLighting();
                return cache.IsComplete && cache.LightingComplete;
            }, building: () => false, reason: () => "The captured source's finite host solve has not completed.");
        }
        Complete();
        var lighting = cache.PublishedLightingSource;
        Assert.NotNull(lighting);
        var snapshot = cache.Snapshot();
        var pick = new SdfIndirectPick(SdfIndirectPickStatus.Resolved, SdfIndirectTier.High, 0, 255,
            Vector3.Zero, new Vector3(0f, .004f, 0f), .004f, Vector3.UnitY,
            (uint)snapshot.PublishedGeneration, snapshot.PublishedStamp, [],
            new SdfIndirectPickSources(default, default, .25f * Vector3.UnitX, default, default), snapshot, null, lighting) {
            Near = SdfIndirectNearOutcome.Hit,
            NearDirection = Vector3.UnitY,
            NearPreviousPublication = cache.PreviousPublishedStamp,
            NearSource = lighting,
            SourcesEnabled = SdfIndirectSources.Emission,
        };
        var reference = WorldIndirectReference.Evaluate(pick, paths: 16);
        Assert.Null(reference.Refusal);
        Assert.NotNull(reference.Estimate);
        Assert.Equal(new IrradianceEstimate(new Double3(.25, 0, 0), 16, 0), reference.Estimate.Value);
        Assert.True(reference.FieldQueries > 0);
        Assert.Equal(16L, reference.Casts);
        Assert.Equal(lighting.Sequence, reference.SourceSequence);
        Assert.Equal(Vector3.Zero, reference.Difference);
        var text = new WorldIndirectPickText().Read(pick, reference);
        Assert.Contains("source-role=current-near-publication", text);
        Assert.Contains("near-direction=0,1,0", text);
        Assert.DoesNotContain("gpu-minus-reference unavailable", text);

        source = source with { IndirectSources = SdfIndirectSources.Emission | SdfIndirectSources.Direct };
        Complete();
        var foreign = cache.PublishedLightingSource;
        Assert.NotNull(foreign);
        Assert.NotSame(lighting, foreign);
        foreach (var unsupported in new[] { pick with { NearSource = null }, pick with { NearSource = foreign } }) {
            var refused = WorldIndirectReference.Evaluate(unsupported, paths: 16);
            Assert.Contains("Near Hit reference needs its sampled direction and incoming source", refused.Refusal);
            Assert.Null(refused.Estimate);
            Assert.Null(refused.Difference);
            Assert.Equal(0L, refused.FieldQueries);
            Assert.Equal(0L, refused.Casts);
        }
    }

    private sealed class CapturingFrameSource(Func<SdfFrame> capture) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => capture();
    }
}
