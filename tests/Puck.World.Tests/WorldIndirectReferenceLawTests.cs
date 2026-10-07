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
        var floor = builder.AddMaterial(material: new SdfMaterial(Vector3.Zero));
        var emitter = builder.AddMaterial(material: new SdfMaterial(Vector3.UnitX, Emissive: 1f, Metal: 1f));

        builder.Plane(Vector3.UnitY, 0f, floor);
        builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: .18f, z: 0f)).Sphere(.025f, emitter);
        var source = new SdfFrame(Program: builder.Build(), ProgramChanged: false, Time: 0f,
            Views: [new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(fieldOfViewRadians: 1f,
                position: new Vector3(x: 0f, y: .1f, z: -1f), target: new Vector3(x: 0f, y: .1f, z: 0f),
                viewportHeight: 16, viewportWidth: 16), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))]) {
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
            frameSource: new CapturingFrameSource(capture: () => source), height: 16, kernels: SdfTestPipelines.Kernels(),
            name: "near-reference", pipelines: SdfTestPipelines.Cache(), width: 16);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var cache = residency.Tables!.Indirect!;

        void Complete() {
            TestLiveness.Within(frames: 64, step: () => {
                residency.BeginFrame();
                Assert.True(condition: residency.Prepare(context: context));
                _ = residency.Submit(context: context);
                // Commit the admitted host work to obtain its actual immutable source. The fake device provides
                // no radiance answer; the red incoming value below follows independently from the emitter.
                cache.Submitted();
                cache.SubmittedLighting();
                return (cache.IsComplete && cache.LightingComplete);
            }, building: () => false, reason: () => "The captured source's finite host solve has not completed.");
        }
        Complete();
        var lighting = cache.PublishedLightingSource;

        Assert.NotNull(@object: lighting);
        var snapshot = cache.Snapshot();
        var pick = new SdfIndirectPick(SdfIndirectPickStatus.Resolved, SdfIndirectTier.High, 0, 255,
            Vector3.Zero, new Vector3(x: 0f, y: .004f, z: 0f), .004f, Vector3.UnitY,
            ((uint)snapshot.PublishedGeneration), snapshot.PublishedStamp, [],
            new SdfIndirectPickSources(default, default, (.25f * Vector3.UnitX), default, default), snapshot, null, lighting) {
            Near = SdfIndirectNearOutcome.Hit,
            NearDirection = Vector3.UnitY,
            NearPreviousPublication = cache.PreviousPublishedStamp,
            NearSource = lighting,
            SourcesEnabled = SdfIndirectSources.Emission,
        };
        var reference = WorldIndirectReference.Evaluate(pick, paths: 16);

        Assert.Null(@object: reference.Refusal);
        Assert.NotNull(value: reference.Estimate);
        Assert.Equal(new IrradianceEstimate(Irradiance: new Double3(X: .25, Y: 0, Z: 0), Paths: 16, Unresolved: 0), reference.Estimate.Value);
        Assert.True(condition: (reference.FieldQueries > 0));
        Assert.Equal(16L, reference.Casts);
        Assert.Equal(lighting.Sequence, reference.SourceSequence);
        Assert.Equal(Vector3.Zero, reference.Difference);
        var text = new WorldIndirectPickText().Read(pick: pick, reference: reference);

        Assert.Contains(actualString: text, expectedSubstring: "source-role=current-near-publication");
        Assert.Contains(actualString: text, expectedSubstring: "near-direction=0,1,0");
        Assert.DoesNotContain(actualString: text, expectedSubstring: "gpu-minus-reference unavailable");

        source = source with { IndirectSources = SdfIndirectSources.Emission | SdfIndirectSources.Direct };
        Complete();
        var foreign = cache.PublishedLightingSource;

        Assert.NotNull(@object: foreign);
        Assert.NotSame(actual: foreign, expected: lighting);
        foreach (var unsupported in new[] { pick with { NearSource = null }, pick with { NearSource = foreign } }) {
            var refused = WorldIndirectReference.Evaluate(unsupported, paths: 16);

            Assert.Contains("Near Hit reference needs its sampled direction and incoming source", refused.Refusal);
            Assert.Null(value: refused.Estimate);
            Assert.Null(value: refused.Difference);
            Assert.Equal(0L, refused.FieldQueries);
            Assert.Equal(0L, refused.Casts);
        }
    }

    private sealed class CapturingFrameSource(Func<SdfFrame> capture) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => capture();
    }
}
