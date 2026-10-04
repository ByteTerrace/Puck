using System.Buffers.Binary;
using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// The sky's environment refreshes only on change: the upload renders the map and its coefficients when the layers its
// lighting sees move while fog, ambient or reflection reads it, and otherwise records nothing, its pass reading skipped. A still sky renders
// once; a body, the stars, the twinkle, the fog's density, the clouds and every other layer only the camera sees leave the
// map as it is; a stop, a lit layer added, and the sky frame render it again; a sky with all consumers disabled renders nothing until a consumer
// reads the map; an installed kernel reload renders it again; and a refresh never overwrites the counts of the refresh two
// uploads before it before the ledger reads them.
public sealed partial class SdfWorldTablesWorkLawTests {
    [Fact]
    public void AStillSkyRendersItsEnvironmentOnceAndThenRecordsNothing() {
        using var rig = new Rig();

        for (var upload = 0; (upload < 4); upload++) {
            rig.Render();
        }

        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);
        Assert.Contains(actualString: rig.Report(), expectedSubstring: "\nwork environment skipped\n");
    }
    [Fact]
    public void OnlyAMoveOfTheLayersTheLightingSeesRendersTheEnvironmentAgain() {
        using var rig = new Rig();
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
        using var rig = new Rig();

        rig.Frame.Sky.Atmosphere.FogDensity = 0f;
        rig.Frame.Sky.Block.Ambient = 0f;
        rig.Frame.Sky.Block.Reflection = 0f;
        rig.Render();
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 0L, actual: rig.Engine.SkyEnvironmentRenders);
        Assert.Contains(actualString: rig.Report(), expectedSubstring: "\nwork environment skipped\n");

        rig.Frame.Sky.Atmosphere.FogDensity = SdfSky.DefaultFogDensity;
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);
    }
    [Fact]
    public void TheAtmosphereOwesTheEnvironmentOnlyWhileItInScattersTheSky() {
        using var rig = new Rig();

        // A fog in-scattering its own colour reads no sky, so it renders no map.
        rig.Frame.Sky.Atmosphere = (SdfAtmosphere.Default with { FogColor = Vector3.One, FogColorAuthored = true });
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 0L, actual: rig.Engine.SkyEnvironmentRenders);
        Assert.Contains(actualString: rig.Report(), expectedSubstring: "\nwork environment skipped\n");

        // A haze in-scatters the sky toward the bodies, so it reads the map with no fog at all.
        rig.Frame.Sky.Atmosphere = (SdfAtmosphere.None with { HazeAmount = 0.3f });
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        // A medium in-scatters its own colour: with the haze gone the map is owed no more, and the one it holds stands.
        rig.Frame.Sky.Atmosphere = (SdfAtmosphere.None with { MediumExtinction = 0.4f });
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);
        Assert.Contains(actualString: rig.Report(), expectedSubstring: "\nwork environment skipped\n");
    }
    [Fact]
    public void AnInstalledKernelReloadRendersTheEnvironmentAgain() {
        using var rig = new Rig();

        rig.Render();
        Assert.Equal(expected: 0, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 1)));
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        Assert.Equal(expected: 1, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 2)));
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 2L, actual: rig.Engine.SkyEnvironmentRenders);
    }
    // A refresh two uploads after another records its counters' copy into the same ring slot's readback, so the ledger
    // must have read the earlier refresh's counts before the later one is submitted. The fake device models a readback as
    // holding what the latest submitted copy into it wrote, and holds every fence until an upload waits it, so nothing
    // reads a slot before its upload is known complete. Refreshes at uploads 1, 3 and 5 each write a count of their own
    // into ring slot 0: each is read before the next refresh into the slot is submitted, and the sample the ledger
    // publishes for each refresh carries its own count in its environment row.
    [Fact]
    public void ARefreshTwoUploadsAfterAnotherNeverOverwritesItsUnreadCounts() {
        using var rig = new Rig(holdFences: true);
        // Per ring slot: the count the latest submitted refresh copied into its readback, and whether the ledger has read it.
        var written = new long[SdfWorldTables.FrameRingSize];
        var unread = new bool[SdfWorldTables.FrameRingSize];
        var overwritten = new List<int>();
        var refreshSamples = new List<string>();
        // The environment row's sky evaluations: the fourth row's third count.
        var offset = (((3 * GpuKernelCounters.RowWords) + (2 * GpuKernelCounters.CountWords)) * sizeof(uint));

        rig.Gpu.WriteReadback = (name, destination) => {
            if (!string.Equals(a: name.Part, b: "sky-environment-counters", comparisonType: StringComparison.Ordinal)) {
                return;
            }

            BinaryPrimitives.WriteInt64LittleEndian(destination: destination[offset..], value: written[name.Index]);
            unread[name.Index] = false;
        };

        for (var upload = 1; (upload <= 7); upload++) {
            var refreshes = ((upload % 2) == 1);

            if (refreshes && (upload > 1)) {
                rig.Frame.Sky.First<SdfSkyGradient>().SetStop(color: new Vector3(value: (0.1f * upload)), elevation: 1f, index: 1);
            }

            rig.Render();
            Assert.Equal(expected: ((upload + 1L) / 2L), actual: rig.Engine.SkyEnvironmentRenders);
            if (refreshes) {
                var slot = ((upload - 1) % SdfWorldTables.FrameRingSize);

                if (unread[slot]) {
                    overwritten.Add(item: upload);
                }

                written[slot] = (1000L * upload);
                unread[slot] = true;
            }

            var report = rig.Report();

            if (report.Contains(comparisonType: StringComparison.Ordinal, value: "work environment executed:")) {
                refreshSamples.Add(item: $"{report[..report.IndexOf(value: '\n')]} {EnvironmentEvaluations().Match(input: report).Value}");
            }
        }

        Assert.Empty(collection: overwritten);
        Assert.Equal(
            actual: refreshSamples.Distinct(),
            expected: [
                "work submission=1 revision=1 sky.evaluations=1000",
                "work submission=3 revision=1 sky.evaluations=3000",
                "work submission=5 revision=1 sky.evaluations=5000",
            ]
        );
    }
    [Fact]
    public void TheEnvironmentKeepsOneMapAndOneSetOfCoefficients() {
        // A 64 by 64 map of four half floats a texel and nine four-float coefficients: one pair a residency, however many
        // views read it.
        Assert.Equal(
            actual: (SdfSkyEnvironment.MapBytes, SdfSkyEnvironment.CoefficientBytes, SdfWorldTables.SkyEnvironmentBytes),
            expected: (65_536, 144, 65_680)
        );
        // Its kernel counters: a counter and a readback buffer a ring slot, a row an upload pass, the environment's plain row
        // and one row each of the sky's detail rows, at their capacity, each row seventeen 64-bit counters, including
        // secondary-shadow pixels, indirect work and primitive evaluations.
        Assert.Equal(expected: 5_032, actual: (((SdfWorldTables.PassLabels.Length + 1) + SdfSkyDetails.Capacity) * GpuKernelCounters.RowBytes));
    }

    // The sky evaluations a report's environment line carries.
    [GeneratedRegex(pattern: @"(?<=work environment executed:[^\n]*)sky\.evaluations=\d+")]
    private static partial Regex EnvironmentEvaluations();
}
