using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void CadenceOwnerNameAloneRunsShadowAndItsConsumers(bool temporal) {
        var lights = SdfLights.Default();

        lights.ShadowSlots.SetOwner(owner: "original", slot: 0);
        using var rig = new TemporalRig(views: 1, cadence: true, temporal: temporal, amortize: true, lights: lights);

        for (var frame = 0; (frame < 12); frame++) { rig.Produce(); }
        Assert.True(condition: rig.Stood());
        rig.Lights.ShadowSlots.SetOwner(owner: "replacement", slot: 0);
        rig.Produce();
        Assert.Equal(expected: ((string[])[SdfWorldPackage.Parts.Shadow, SdfWorldPackage.Parts.Views,
            .. (temporal ? new[] { SdfWorldPackage.Resolve } : []), SdfWorldPackage.Parts.Sky, SdfWorldPackage.Parts.Composite]),
            actual: Executed(work: CadenceWork(rig: rig)));
        Assert.Empty(collection: rig.StateConflicts);
        for (var frame = 0; (frame < 12); frame++) { rig.Produce(); }
        Assert.True(condition: rig.Stood());
    }
    [InlineData("drift", false)]
    [InlineData("twinkle", false)]
    [InlineData("fog-color", false)]
    [InlineData("volume", false)]
    [InlineData("fog-density", false)]
    [InlineData("haze", false)]
    [InlineData("medium", false)]
    [InlineData("light-color", false)]
    [InlineData("indirect-method", false)]
    [InlineData("shadow-direction", false)]
    [InlineData("camera", false)]
    [InlineData("drift", true)]
    [InlineData("fog-density", true)]
    [InlineData("haze", true)]
    [InlineData("medium", true)]
    [InlineData("light-color", true)]
    [InlineData("indirect-method", true)]
    [InlineData("shadow-direction", true)]
    [InlineData("camera", true)]
    [Theory]
    public void CadenceRunsExactlyTheChangedClass(string change, bool reduced) {
        using var rig = new TemporalRig(sky: Layered, views: 1, cadence: true, renderScale: (reduced ? 0.25f : 1f));
        var frame = rig.SourceFrame;
        string[] executed = [SdfWorldPackage.Parts.Sky, SdfWorldPackage.Parts.Composite];

        switch (change) {
            case "drift": frame.Sky.First<SdfSkyClouds>().DriftOffset = new Vector2(x: 0.125f, y: 0.25f); break;
            case "twinkle": frame.Sky.First<SdfSkyStars>().TwinklePhase = 0.37f; break;
            case "fog-color": frame.Sky.First<SdfSkyGradient>().SetStop(color: new Vector3(x: 0.2f, y: 0.1f, z: 0.3f), elevation: -1f, index: 0); break;
            case "volume":
                frame = frame with {
                    Volumes = [new SdfVolume(Kind: SdfVolumeKind.Cloud, Position: Vector3.Zero,
                Rotation: Quaternion.Identity, HalfExtent: Vector3.One, DynamicSlot: SdfProgram.NoDynamicTransformSlot,
                Axis: 1f, Width: 1f, Speed: 0.5f, Seed: 123u, Steps: 8,
                Ramp: [new SdfDensityStop(Density: 0f, Color: Vector3.Zero), new SdfDensityStop(Density: 1f, Color: Vector3.One)],
                Intensity: 1f, Extinction: 0.5f)],
                }; break;
            case "fog-density": frame.Sky.Atmosphere.FogDensity = 0.08f; goto case "lighting";
            case "haze": frame.Sky.Atmosphere.HazeAmount = 0.2f; goto case "lighting";
            case "medium": frame.Sky.Atmosphere.MediumExtinction = 0.2f; goto case "lighting";
            case "light-color": frame.Lights.Set(index: 0, light: frame.Lights[0] with { Color = new Vector3(x: 0.2f, y: 0.4f, z: 0.6f) }); goto case "lighting";
            case "indirect-method":
                frame = frame with { Views = [frame.Views[0] with { Quality = frame.Views[0].Quality with { IndirectMethod = SdfIndirectMethod.Screen } }] };
                goto case "lighting";
            case "lighting": executed = [SdfWorldPackage.Parts.Views, .. (reduced ? new[] { SdfWorldPackage.Resolve } : []), .. executed]; break;
            case "shadow-direction":
                frame.Lights.Set(index: 0, light: frame.Lights[0] with { Direction = new Vector3(x: 0.8f, y: 0.2f, z: 0.1f) });
                executed = [SdfWorldPackage.Parts.Shadow, SdfWorldPackage.Parts.Views, .. (reduced ? new[] { SdfWorldPackage.Resolve } : []), .. executed];
                break;
            case "camera":
                frame = frame with { Views = [frame.Views[0] with { Camera = CameraSnapshot.LookAt(position: new Vector3(x: 1f, y: 0f, z: -5f), target: Vector3.Zero, fieldOfViewRadians: 1f, viewportWidth: Extent, viewportHeight: Extent) }] };
                executed = [SdfWorldPackage.Parts.Mask, SdfWorldPackage.Parts.Beam, SdfWorldPackage.Parts.Tape, SdfWorldPackage.Parts.CullArgs,
                    SdfWorldPackage.Parts.Primary, SdfWorldPackage.Parts.Surface, SdfWorldPackage.Parts.Ambient,
                    SdfWorldPackage.Parts.Shadow, SdfWorldPackage.Parts.Views, .. (reduced ? new[] { SdfWorldPackage.Resolve } : []), .. executed];
                break;
        }
        rig.SourceFrame = frame with { };
        rig.Produce();
        var work = CadenceWork(rig: rig);

        Assert.Equal(expected: executed, actual: Executed(work: work));
        var dispatches = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.Dispatches);
        var indirect = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.IndirectDispatches);
        var barriers = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.BufferBarriers);

        for (var pass = 0; (pass < work.PassLabels.Length); pass++) {
            if (work.GetPassState(pass: pass) == GpuPassState.Executed) {
                _ = work.TryGetPassCount(column: dispatches, pass: pass, value: out var directCount);
                _ = work.TryGetPassCount(column: indirect, pass: pass, value: out var indirectCount);
                Assert.Equal(actual: (directCount + indirectCount), expected: 1L);
                if (work.PassLabels[pass].EndsWith(comparisonType: StringComparison.Ordinal, value: "$views")) {
                    var transition = (work.TryGetPassCount(column: barriers, pass: pass, value: out var count) && (count > 0));

                    Assert.Equal(actual: transition, expected: (change is "camera" or "shadow-direction"));
                }
            } else {
                Assert.False(condition: work.TryGetPassCount(column: barriers, pass: pass, value: out _));
                Assert.False(condition: work.TryGetPassCount(column: dispatches, pass: pass, value: out _));
            }
        }
        Assert.Empty(collection: rig.StateConflicts);
        rig.Produce();
        Assert.True(condition: rig.Stood());
    }
    [Fact]
    public void CadenceVisualChangesLeaveConvergedHistoryAndItsRingStanding() {
        using var rig = new TemporalRig(sky: Layered, views: 1, cadence: true, temporal: true);

        for (var frame = 0; (frame < 12); frame++) { rig.Produce(); }
        Assert.True(condition: rig.Stood());
        var frames = rig.HistoryFrames();

        for (var frame = 0; (frame < 12); frame++) {
            rig.SourceFrame.Sky.First<SdfSkyClouds>().DriftOffset = new Vector2(x: (frame + 0.1f), y: 0f);
            rig.SourceFrame = rig.SourceFrame with { };
            rig.Produce();
            Assert.Equal(expected: new[] { SdfWorldPackage.Parts.Sky, SdfWorldPackage.Parts.Composite }, actual: Executed(work: CadenceWork(rig: rig)));
            // The last resolve block precedes its completed sample; a sky-only block sees that completed count.
            Assert.Equal(expected: (frames + 1u), actual: rig.HistoryFrames());
        }
        Assert.Empty(collection: rig.StateConflicts);
    }
    [Fact]
    public void CadenceLightingChangesOweAFullTemporalSettlingPeriod() {
        using var rig = new TemporalRig(sky: Layered, views: 1, cadence: true, temporal: true);

        for (var frame = 0; (frame < 12); frame++) { rig.Produce(); }
        Assert.True(condition: rig.Stood());
        rig.SourceFrame.Lights.Set(index: 0, light: rig.SourceFrame.Lights[0] with { Color = new Vector3(x: 0.3f, y: 0.5f, z: 0.7f) });
        rig.SourceFrame = rig.SourceFrame with { };
        for (var sample = 0; (sample < SdfTemporalHistory.Period); sample++) {
            rig.Produce();
            Assert.False(condition: rig.Stood(), userMessage: $"lighting sample {sample}");
            Assert.Contains(SdfWorldPackage.Resolve, Executed(work: CadenceWork(rig: rig)));
        }
        rig.Produce();
        Assert.True(condition: rig.Stood());
    }
    [Fact]
    public void CadenceCanBeDisabledAndDoesNotAllocateOnAStillView() {
        using var rig = new TemporalRig(sky: Layered, views: 1, cadence: true);

        for (var frame = 0; (frame < 8); frame++) { rig.Produce(); }
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => rig.Produce()));
        rig.SourceFrame.Sky.First<SdfSkyClouds>().DriftOffset = new Vector2(x: 0.1f, y: 0f);
        rig.SourceFrame = rig.SourceFrame with { };
        rig.Produce();
        Assert.Equal(expected: 2, actual: Executed(work: CadenceWork(rig: rig)).Length);
        rig.SourceFrame = rig.SourceFrame with { EnableCadenceGate = false };
        rig.Produce();
        Assert.Equal(expected: new[] { SdfWorldPackage.Parts.Mask, SdfWorldPackage.Parts.Beam, SdfWorldPackage.Parts.Tape,
            SdfWorldPackage.Parts.CullArgs, SdfWorldPackage.Parts.Primary, SdfWorldPackage.Parts.Surface,
            SdfWorldPackage.Parts.Ambient, SdfWorldPackage.Parts.Shadow, SdfWorldPackage.Parts.Views,
            SdfWorldPackage.Parts.Sky, SdfWorldPackage.Parts.Composite }, actual: Executed(work: CadenceWork(rig: rig)));
    }
    [Fact]
    public void CadenceRunsTheSkyEveryFrameWhileALayerSamplesAScreen() {
        using var rig = new TemporalRig(sky: static sky => { _ = sky.Add(label: "panorama", parameters: new SdfSkyPanorama { Intensity = 1f, Screen = 0 }); }, views: 1, cadence: true);

        for (var frame = 0; (frame < 3); frame++) {
            rig.Produce();
            Assert.False(condition: rig.Stood(), userMessage: $"frame {frame}");
            Assert.Equal(expected: new[] { SdfWorldPackage.Parts.Sky, SdfWorldPackage.Parts.Composite }, actual: Executed(work: CadenceWork(rig: rig)));
        }
    }
    [Fact]
    public void CadenceAtTheFloorLeavesMarchAndShadowStandingOnDrift() {
        using var rig = new TemporalRig(sky: Layered, views: 1, cadence: true, renderScale: 0.25f);

        rig.SourceFrame = rig.SourceFrame with { Views = [rig.SourceFrame.Views[0] with { Quality = new SdfViewQuality { DisableAmbientOcclusion = true, DisableSoftShadows = true } }] };
        rig.Produce();
        Assert.Equal(expected: new[] { SdfWorldPackage.Parts.Mask, SdfWorldPackage.Parts.Beam, SdfWorldPackage.Parts.Tape,
            SdfWorldPackage.Parts.CullArgs, SdfWorldPackage.Parts.Primary, SdfWorldPackage.Parts.Surface,
            SdfWorldPackage.Parts.Views, SdfWorldPackage.Resolve, SdfWorldPackage.Parts.Sky,
            SdfWorldPackage.Parts.Composite }, actual: Executed(work: CadenceWork(rig: rig)));
        rig.SourceFrame.Sky.First<SdfSkyClouds>().DriftOffset = new Vector2(x: 0.15f, y: 0f);
        rig.SourceFrame = rig.SourceFrame with { };
        rig.Produce();
        Assert.Equal(expected: new[] { SdfWorldPackage.Parts.Sky, SdfWorldPackage.Parts.Composite }, actual: Executed(work: CadenceWork(rig: rig)));
    }
    [InlineData("ambient", false)]
    [InlineData("reflection", false)]
    [InlineData("gradient", false)]
    [InlineData("sub-code-gradient", false)]
    [InlineData("panel", false)]
    [InlineData("panel-frame", false)]
    [InlineData("camera-panel", false)]
    [InlineData("ambient", true)]
    [InlineData("reflection", true)]
    [InlineData("gradient", true)]
    [InlineData("sub-code-gradient", true)]
    [InlineData("panel", true)]
    [InlineData("panel-frame", true)]
    [InlineData("camera-panel", true)]
    [Theory]
    public void CadenceTracksSkyLightingAtItsReaders(string change, bool reduced) {
        using var rig = new TemporalRig(views: 1, cadence: true, renderScale: (reduced ? 0.25f : 1f), sky: sky => {
            sky.First<SdfSkyGradient>().SetStop(color: new Vector3(value: 0.25f), elevation: -1f, index: 0);
            sky.First<SdfSkyGradient>().SetStop(color: new Vector3(value: 0.25f), elevation: 1f, index: 1);
            _ = sky.Add(label: "panel", blend: SdfSkyBlend.Add,
                visibility: ((change == "camera-panel") ? SdfSkyVisibility.Camera : SdfSkyVisibility.Lighting),
                parameters: new SdfSkyPanel { Direction = Vector3.UnitZ, Size = new Vector2(value: 0.2f), Color = Vector3.One, Intensity = 1f });
        });
        var sky = rig.SourceFrame.Sky;
        var renders = rig.Selected.Tables!.SkyEnvironmentRenders;

        switch (change) {
            case "ambient": sky.Block.Ambient = 0.5f; break;
            case "reflection": sky.Block.Reflection = 0.5f; break;
            case "gradient":
            case "sub-code-gradient":
                var color = new Vector3(value: ((change == "gradient") ? 0.5f : 0.25001f));
                sky.First<SdfSkyGradient>().SetStop(color: color, elevation: -1f, index: 0);
                sky.First<SdfSkyGradient>().SetStop(color: color, elevation: 1f, index: 1);
                break;
            case "panel-frame": sky.FrameUp = Vector3.Normalize(value: new Vector3(x: 0.00001f, y: 1f, z: 0f)); break;
            default: sky.First<SdfSkyPanel>().Intensity += 0.00001f; break;
        }
        rig.SourceFrame = rig.SourceFrame with { };
        rig.Produce();
        Assert.Equal(expected: (renders + ((change == "gradient") ? 1 : 0)), actual: rig.Selected.Tables.SkyEnvironmentRenders);
        var lit = (change is not ("sub-code-gradient" or "camera-panel"));
        string[] expected = [.. (lit ? new[] { SdfWorldPackage.Parts.Views } : []),
            .. ((lit && reduced) ? new[] { SdfWorldPackage.Resolve } : []), SdfWorldPackage.Parts.Sky, SdfWorldPackage.Parts.Composite];

        Assert.Equal(expected: expected, actual: Executed(work: CadenceWork(rig: rig)));
        Assert.Empty(collection: rig.StateConflicts);
        rig.Produce();
        Assert.True(condition: rig.Stood());
    }

    // A sky whose stack drifts and twinkles over the default gradient: a point run of stars and a field run of clouds.
    private static void Layered(SdfSky sky) {
        sky.LayerAt(index: 0).Visibility = SdfSkyVisibility.Camera;
        _ = sky.Add(blend: SdfSkyBlend.Add, label: "stars", parameters: new SdfSkyStars { Brightness = 1f });
        _ = sky.Add(label: "clouds", parameters: new SdfSkyClouds { Coverage = 0.5f });
    }
    private static GpuWorkSample CadenceWork(TemporalRig rig) {
        var sample = new GpuWorkSample();

        rig.World.PollReadbacks();
        Assert.True(condition: rig.World.TryReadCompleted(sample: sample));
        return sample;
    }
    private static string[] Executed(GpuWorkSample work) => [.. Enumerable.Range(start: 0, count: work.PassLabels.Length)
        .Where(predicate: pass => (work.GetPassState(pass: pass) == GpuPassState.Executed))
        .Select(selector: pass => work.PassLabels[pass].Split('$')[^1])];
}
