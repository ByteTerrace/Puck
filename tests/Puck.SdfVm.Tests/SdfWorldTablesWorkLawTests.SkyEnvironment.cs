using System.Buffers.Binary;
using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// The sky's environment refreshes only on change: the upload renders the map and its coefficients when the sky's gradient
// moves while the fog reads it, and otherwise records nothing, its pass reading skipped. A still sky renders once; a body,
// the stars, the twinkle, the fog's density and the clouds leave the map as it is; a stop renders it again; an unfogged
// sky renders nothing until its fog reads the map; an installed kernel reload renders it again; and a refresh never
// overwrites the counts of the refresh two uploads before it before the ledger reads them.
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
    public void OnlyAMoveOfTheGradientTheFogReadsRendersTheEnvironmentAgain() {
        using var rig = new Rig();
        var sky = rig.Frame.Sky;

        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        // Everything but the gradient: the twinkle's phase, the stars, a disc, the fog's density, the studio horizon and a
        // cloud layer, covering the sky and drifting.
        sky.Block.TwinklePhase = 0.25f;
        sky.Block.StarBrightness = 2f;
        sky.Block.DiscLight = 0;
        sky.Block.FogDensity = (2f * SdfSky.DefaultFogDensity);
        sky.Block.HorizonLow = Vector3.One;
        sky.Block.CloudCoverage = 0.5f;
        rig.Render();
        sky.Block.CloudDriftOffset = new Vector2(x: 3f, y: 1f);
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        // A stop moves the gradient.
        sky.SetStop(index: 1, stop: new SdfSkyStop(Color: new Vector3(x: 0.2f, y: 0.4f, z: 0.9f), Elevation: 1f));
        rig.Render();
        Assert.Equal(expected: 2L, actual: rig.Engine.SkyEnvironmentRenders);

        // A stop in use beyond the last moves it; one past the stops in use does not.
        sky.StopCount = 3;
        sky.SetStop(index: 2, stop: new SdfSkyStop(Color: Vector3.One, Elevation: 1f));
        rig.Render();
        sky.SetStop(index: 3, stop: new SdfSkyStop(Color: Vector3.Zero, Elevation: 1f));
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 3L, actual: rig.Engine.SkyEnvironmentRenders);
    }
    [Fact]
    public void AnUnfoggedSkyRendersNoEnvironmentUntilItsFogReadsIt() {
        using var rig = new Rig();

        rig.Frame.Sky.Block.FogDensity = 0f;
        rig.Render();
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 0L, actual: rig.Engine.SkyEnvironmentRenders);
        Assert.Contains(actualString: rig.Report(), expectedSubstring: "\nwork environment skipped\n");

        rig.Frame.Sky.Block.FogDensity = SdfSky.DefaultFogDensity;
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);
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
                rig.Frame.Sky.SetStop(index: 1, stop: new SdfSkyStop(Color: new Vector3(value: (0.1f * upload)), Elevation: 1f));
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
            expected: (32_768, 144, 32_912)
        );
        // Four upload-pass rows, each with three shared and six shadow-slot 64-bit counters, in each buffer.
        Assert.Equal(expected: 288, actual: (SdfWorldTables.PassLabels.Length * GpuKernelCounters.RowBytes));
    }

    // The sky evaluations a report's environment line carries.
    [GeneratedRegex(pattern: @"(?<=work environment executed:[^\n]*)sky\.evaluations=\d+")]
    private static partial Regex EnvironmentEvaluations();
}
