using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// The sky's environment refreshes only on change: the graph producer renders the map and its coefficients when the layers its
// lighting sees move while fog, ambient or reflection reads it, and otherwise records nothing. A still sky renders
// once; a body, the stars, the twinkle, the fog's density, the clouds and every other layer only the camera sees leave the
// map as it is; a stop, a lit layer added, and the sky frame render it again; a sky with all consumers disabled renders nothing until a consumer
// reads the map; an installed kernel reload renders it again; and a refresh never overwrites an earlier slot's counts
// before the ledger reads them.
public sealed partial class SdfWorldTablesWorkLawTests {
    [Fact]
    public void AStillSkyRendersItsEnvironmentOnceAndThenRecordsNothing() {
        using var rig = new EnvironmentRig();

        for (var upload = 0; (upload < 4); upload++) {
            rig.Render();
        }

        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);
        rig.AssertStanding();
    }
    [Fact]
    public void OnlyAMoveOfTheLayersTheLightingSeesRendersTheEnvironmentAgain() {
        using var rig = new EnvironmentRig();
        var sky = rig.Frame.Sky;

        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        // Everything the lighting does not see: twinkling stars, a disc the lighting sees too (a body never enters the
        // map), the fog's density, a cloud layer, covering the sky and drifting.
        _ = sky.Add(blend: SdfSkyBlend.Add, label: "stars", parameters: new SdfSkyStars { Brightness = 2f, TwinklePhase = 0.25f });
        _ = sky.Add(blend: SdfSkyBlend.Add, label: "disc", parameters: new SdfSkyDisc { Intensity = 1f, Light = 0 }, visibility: SdfSkyVisibility.Both);
        var clouds = sky.Add(label: "clouds", parameters: new SdfSkyClouds { Coverage = 0.5f });

        sky.Atmosphere.FogDensity = (2f * SdfSky.DefaultFogDensity);
        rig.Render();
        sky.Parameters<SdfSkyClouds>(index: clouds).DriftOffset = new Vector2(x: 3f, y: 1f);
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        // A stop moves the gradient.
        ref var gradient = ref sky.First<SdfSkyGradient>();

        gradient.SetStop(color: new Vector3(x: 0.2f, y: 0.4f, z: 0.9f), elevation: 1f, index: 1);
        rig.Render();
        Assert.Equal(expected: 2L, actual: rig.Engine.SkyEnvironmentRenders);

        // A stop extending the gradient moves it; one past the stops in use does not.
        gradient.SetStop(color: new Vector3(x: 0.2f, y: 0.4f, z: 0.9f), elevation: 0f, index: 1);
        gradient.Count = 3u;
        gradient.SetStop(color: Vector3.One, elevation: 1f, index: 2);
        rig.Render();
        gradient.SetStop(color: Vector3.Zero, elevation: 1f, index: 3);
        rig.Render();
        Assert.Equal(expected: 3L, actual: rig.Engine.SkyEnvironmentRenders);

        // A layer the lighting sees joins the map; the sky frame turns it.
        _ = sky.Add(label: "haze", parameters: new SdfSkyNoise { ColorHigh = Vector3.One, ColorLow = Vector3.Zero, Coverage = 1f, Gain = 0.5f, Octaves = 4u, Scale = 4f }, visibility: SdfSkyVisibility.Lighting);
        rig.Render();
        Assert.Equal(expected: 4L, actual: rig.Engine.SkyEnvironmentRenders);
        sky.FrameUp = new Vector3(x: 0.2f, y: 1f, z: 0f);
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 5L, actual: rig.Engine.SkyEnvironmentRenders);
    }
    [Fact]
    public void DisabledEnvironmentRendersNothingUntilAConsumerReadsIt() {
        using var rig = new EnvironmentRig();

        rig.Frame.Sky.Atmosphere.FogDensity = 0f;
        rig.Frame.Sky.Block.Ambient = 0f;
        rig.Frame.Sky.Block.Reflection = 0f;
        rig.Render();
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 0L, actual: rig.Engine.SkyEnvironmentRenders);
        rig.AssertStanding();

        rig.Frame.Sky.Atmosphere.FogDensity = SdfSky.DefaultFogDensity;
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);
    }
    [Fact]
    public void TheAtmosphereOwesTheEnvironmentOnlyWhileItInScattersTheSky() {
        using var rig = new EnvironmentRig();

        // Isolate atmosphere from the two surface-lighting consumers. A fog in-scattering its own colour reads no sky.
        rig.Frame.Sky.Block.Ambient = 0f;
        rig.Frame.Sky.Block.Reflection = 0f;
        rig.Frame.Sky.Atmosphere = (SdfAtmosphere.Default with { FogColor = Vector3.One, FogColorAuthored = true });
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 0L, actual: rig.Engine.SkyEnvironmentRenders);
        rig.AssertStanding();

        // A haze in-scatters the sky toward the bodies, so it reads the map with no fog at all.
        rig.Frame.Sky.Atmosphere = (SdfAtmosphere.None with { HazeAmount = 0.3f });
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        // A medium in-scatters its own colour: with the haze gone the map is owed no more, and the one it holds stands.
        rig.Frame.Sky.Atmosphere = (SdfAtmosphere.None with { MediumExtinction = 0.4f });
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);
        rig.AssertStanding();
    }
    [Fact]
    public void AnInstalledKernelReloadRendersTheEnvironmentAgain() {
        using var rig = new EnvironmentRig();

        rig.Render();
        Assert.Equal(expected: 0, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 1)));
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        Assert.Equal(expected: 1, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 2)));
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 2L, actual: rig.Engine.SkyEnvironmentRenders);
    }
    // The graph has three recording slots. Hold every fence until slot reuse waits for it, and model each readback
    // as the last copy into that slot. Seven successive projections must retire all earlier counts before overwriting.
    [Fact]
    public void ARefreshNeverOverwritesItsUnreadGraphCounts() {
        using var rig = new EnvironmentRig(holdFences: true);
        rig.Render();
        rig.Complete();
        var written = new long[3];
        var unread = new bool[3];
        var observed = new List<long>();
        var kind = GpuWork.KernelKinds.IndexOf(GpuWork.SkyEvaluations);
        rig.Gpu.WriteReadback = (name, destination) => {
            if (name.Owner != SdfTestView.EnvironmentInstance || name.Part != "kernel counters") { return; }
            if (unread[name.Index]) { observed.Add(written[name.Index]); }
            BinaryPrimitives.WriteInt64LittleEndian(destination[(kind * sizeof(long))..], written[name.Index]);
            unread[name.Index] = false;
        };

        for (var projection = 1; projection <= 7; projection++) {
            var slot = (int)(rig.Node.FrameCounter % 3);
            rig.Frame.Sky.First<SdfSkyGradient>().SetStop(color: new Vector3(0.1f * projection), elevation: 1f, index: 1);
            rig.Render();
            Assert.Equal(expected: projection + 1L, actual: rig.Engine.SkyEnvironmentRenders);
            Assert.False(condition: unread[slot], userMessage: $"Projection {projection} overwrote unread slot {slot}.");
            written[slot] = 1000L * projection;
            unread[slot] = true;
        }
        rig.Complete();
        Assert.Equal(expected: Enumerable.Range(1, 7).Select(static index => 1000L * index), actual: observed.Order());
        var sample = new GpuWorkSample();
        Assert.True(condition: rig.Node.TryReadCompleted(sample: sample));
        Assert.True(condition: sample.TryGetPassCount(0, GpuWork.SubmissionKinds.IndexOf(GpuWork.SkyEvaluations), out var evaluations));
        Assert.Equal(expected: 7000L, actual: evaluations);
    }
    [Fact]
    public void NewLayerIdentitiesGrowCompletedEnvironmentSlotsAndRetainEarlierSamples() {
        using var rig = new EnvironmentRig(holdFences: true);
        rig.Render();
        rig.Complete();
        var earlier = new GpuWorkSample();
        Assert.True(condition: rig.Node.TryReadCompleted(sample: earlier));
        var oldLabels = rig.Engine.SkyDetails.Labels.ToArray();
        var beforeBytes = rig.Node.OwnedBytes;
        for (var index = 0; index < 40; index++) { _ = rig.Engine.SkyDetails.RowOf($"retired-{index}"); }
        var lastRow = rig.Engine.SkyDetails.RowOf("retired-39");
        // Two passes, the map's plain detail row, then each retained layer identity.
        var expectedBytes = (3 + rig.Engine.SkyDetails.Labels.Count) * GpuKernelCounters.RowBytes;
        var reads = new List<int>();
        rig.Gpu.WriteReadback = (name, destination) => {
            if (name.Owner != SdfTestView.EnvironmentInstance || name.Part != "kernel counters") { return; }
            reads.Add(destination.Length);
            Assert.Equal(expected: expectedBytes, actual: destination.Length);
            var kind = GpuWork.KernelKinds.IndexOf(GpuWork.SkyEvaluations);
            BinaryPrimitives.WriteInt64LittleEndian(destination[(kind * sizeof(long))..], 70);
            BinaryPrimitives.WriteInt64LittleEndian(destination[((3 + (int)lastRow) * GpuKernelCounters.RowBytes + kind * sizeof(long))..], 70);
        };
        // Visit every recording slot; each new pair's actual bytes enter the graph's account.
        for (var index = 1; index <= 3; index++) {
            rig.Frame.Sky.First<SdfSkyGradient>().SetStop(color: new Vector3(0.2f * index), elevation: 1f, index: 1);
            rig.Render();
            rig.Complete();
        }
        Assert.Equal(expected: new[] { expectedBytes, expectedBytes, expectedBytes }, actual: reads);
        Assert.Equal(expected: oldLabels, actual: earlier.Details.ToArray().Skip(1).Select(static detail => detail.Detail));
        // The first slot already had the original details; the other two held only the two pass rows.
        var originalCounterBytes = 2UL * (ulong)GpuKernelCounters.RowBytes * (ulong)(3 + oldLabels.Length + 2 + 2);
        Assert.Equal(expected: beforeBytes - originalCounterBytes + 6UL * (ulong)expectedBytes, actual: rig.Node.OwnedBytes);
        Assert.Equal(expected: rig.Node.OwnedBytes, actual: rig.Node.InstalledAccount.SteadyBytes);
        var sample = new GpuWorkSample();
        Assert.True(condition: rig.Node.TryReadCompleted(sample: sample));
        Assert.True(condition: sample.TryGetDetailCount((int)lastRow + 1, GpuWork.SubmissionKinds.IndexOf(GpuWork.SkyEvaluations), out var evaluations));
        Assert.Equal(expected: 70L, actual: evaluations);
        Assert.Equal(expected: "retired-39", actual: sample.Details[(int)lastRow + 1].Detail);
    }
    [Fact]
    public void TheEnvironmentKeepsOneMapAndOneSetOfCoefficients() {
        // A 64 by 64 map of four half floats a texel and nine four-float coefficients: one pair a residency, however many
        // views read it.
        Assert.Equal(
            actual: (SdfSkyEnvironment.MapBytes, SdfSkyEnvironment.CoefficientBytes, SdfWorldTables.SkyEnvironmentBytes),
            expected: (65_536, 144, 65_680)
        );
    }
}
