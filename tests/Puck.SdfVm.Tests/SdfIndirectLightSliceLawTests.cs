using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Light-map submissions preserve every texel and publish only complete, single-generation maps.</summary>
public sealed class SdfIndirectLightSliceLawTests {
    private static readonly SdfLightRegion Region = new(new Double3(X: -4, Y: -1, Z: -4), new Double3(X: 4, Y: 3, Z: 4));

    [Fact]
    public void EveryAlignedMapIntervalRoundTripsWithoutGrowingThePassBlock() {
        var words = new HashSet<uint>();

        for (var map = 0; (map < SdfIndirectLightLayout.MaxMaps); map++) {
            for (var first = 0; (first < 512); first += 8) {
                for (var count = 8; (count <= (512 - first)); count += 8) {
                    var word = SdfIndirectLightLayout.PackSlice(firstRow: first, map: map, rowCount: count);

                    Assert.True(condition: words.Add(item: word));
                    Assert.True(condition: SdfIndirectLightLayout.TryUnpackSlice(columnCount: out _, firstColumn: out _, firstRow: out var decodedFirst, map: out var decodedMap, rowCount: out var decodedCount, slice: word));
                    Assert.Equal(actual: (decodedMap, decodedFirst, decodedCount), expected: (map, first, count));
                }
            }
        }
        Assert.Equal(24_960, words.Count);
        foreach (var parameters in new[] { SdfWorldInterfaces.WorldParameters, SdfWorldInterfaces.IndirectParameters }) {
            var slice = Assert.Single(collection: parameters.Interface.Members, predicate: member => (member.Name == SdfWorldPackage.LightSlice));

            Assert.Equal(ShaderValueType.Uint, slice.Type);
            Assert.Null(value: slice.Length);
            // The generated package sorts value names: replacing the selector moves offsets, but the packed
            // row interval occupies the same single word and cannot grow any upload block.
            var previous = ShaderPipelineParameterLayout.ForPackage(package: "light-slice-reference", config: null,
                members: [.. parameters.Interface.Members.Where(predicate: member => ((member.Group != ShaderInterfaceGroup.Frame) && (member.Name != ShaderFrameInterface.Extent)))
                    .Select(selector: member => ((member.Name == SdfWorldPackage.LightSlice) ? member with { Name = "lightMap" } : member))]);

            Assert.Equal(previous.SizeBytes, parameters.SizeBytes);
            Assert.Equal(previous.FrameBlockSizeBytes, parameters.FrameBlockSizeBytes);
        }
        foreach (var word in new uint[] { 0, 1, 1024, 1037, 0x20000 | 1025, uint.MaxValue, 1 | (65 << 10), 1 | (63 << 4) | (2 << 10) }) {
            Assert.False(condition: SdfIndirectLightLayout.TryUnpackSlice(columnCount: out _, firstColumn: out _, firstRow: out var first, map: out var map, rowCount: out var count, slice: word));
            Assert.Equal(actual: (map, first, count), expected: (-1, 0, 0));
        }
    }
    [InlineData(-1, 0, 8)]
    [InlineData(12, 0, 8)]
    [InlineData(0, -8, 8)]
    [InlineData(0, 512, 8)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 504, 16)]
    [InlineData(0, 1, 8)]
    [InlineData(0, 0, 9)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    [Theory]
    public void InvalidIntervalsAreRefusedBeforePacking(int map, int first, int count) =>
        Assert.ThrowsAny<ArgumentException>(testCode: () => SdfIndirectLightLayout.PackSlice(firstRow: first, map: map, rowCount: count));
    [InlineData(SdfIndirectTier.Medium, 8)]
    [InlineData(SdfIndirectTier.High, 48)]
    [Theory]
    public void PrimaryAndRepeatedBeamFitTheExistingEvaluationCeiling(SdfIndirectTier tier, int rows) {
        var layout = new SdfIndirectLayout(tier: tier);
        var source = Source(file: "march/sdf-march-constants.hlsli");

        int Limit(string name) => int.Parse(Regex.Match(input: source, pattern: $@"\b{name} = (\d+);").Groups[1].Value,
            provider: System.Globalization.CultureInfo.InvariantCulture);
        var beamSteps = ((Limit(name: "ConeMarchSteps") + Limit(name: "TileGapSteps")) + Limit(name: "TileFarSteps"));
        var beamTiles = (SdfIndirectLightLayout.Resolution / ((int)SdfWorldPackage.TileSize));
        var primary = ((rows * SdfIndirectLightLayout.Resolution) * SdfIndirectLightLayout.MarchSteps);

        Assert.Equal(rows, SdfIndirectLightViews.RowsPerSubmission(layout: layout));
        Assert.InRange((primary + ((beamTiles * beamTiles) * beamSteps)), 1, layout.TraceEvaluationCeiling);
        Assert.True(condition: ((((rows + SdfIndirectLightLayout.SliceRowEdge) * SdfIndirectLightLayout.Resolution) * SdfIndirectLightLayout.MarchSteps) > layout.TraceEvaluationCeiling));
        Assert.Equal(0, SdfIndirectLightViews.RowsPerSubmission(layout: new SdfIndirectLayout(tier: SdfIndirectTier.Off)));
    }
    [InlineData(SdfIndirectTier.Medium)]
    [InlineData(SdfIndirectTier.High)]
    [Theory]
    public void EveryRowSubmitsOnceBeforeItsMapBecomesValid(SdfIndirectTier tier) {
        var views = new SdfIndirectLightViews();
        var layout = new SdfIndirectLayout(tier: tier);
        var lights = Lights();
        var owner = new object();
        var rows = new int[2, SdfIndirectLightLayout.Resolution];
        var frame = 0L;

        for (var batch = 0; ((batch < 128) && (views.Publications < 2)); batch++) {
            void Plan() => views.Plan(frame++, owner, default, lights, [Region, Region], Region, false, layout);
            Plan();
            var pending = views.Pending;
            var slice = views.Slice;
            var revision = views.Revision;

            Assert.InRange(actual: pending, high: 1, low: 0);
            Assert.False(condition: views.Snapshot(index: pending).Valid);
            Plan();
            Assert.Equal(slice, views.Slice);
            Assert.Equal(revision, views.Revision);
            Assert.True(condition: SdfIndirectLightLayout.TryUnpackSlice(columnCount: out _, firstColumn: out _, firstRow: out var first, map: out var map, rowCount: out var count, slice: slice));
            Assert.Equal(actual: map, expected: pending);
            Assert.InRange(count, 8, SdfIndirectLightViews.RowsPerSubmission(layout: layout));
            for (var row = first; (row < (first + count)); row++) { Assert.Equal(1, ++rows[map, row]); }
            var publications = views.Publications;
            var completed = views.Submitted();

            Assert.Equal(actual: completed, expected: ((first + count) == 512));
            Assert.Equal(completed, views.Snapshot(index: map).Valid);
            Assert.Equal((publications + (completed ? 1UL : 0UL)), views.Publications);
            Assert.True(condition: (views.Revision > revision));
            Assert.Equal(0u, views.Slice);
            Assert.False(condition: views.Submitted());
        }
        Assert.Equal(2UL, views.Publications);
        foreach (var visits in rows) { Assert.Equal(actual: visits, expected: 1); }
        views.Plan(casters: Region, forceGeometry: false, frame: frame, geometry: default, geometryOwner: owner, layout: layout, lights: lights, regions: [Region, Region]);
        Assert.Equal(-1, views.Pending);
    }
    [InlineData("geometry")]
    [InlineData("allocation")]
    [InlineData("owner")]
    [InlineData("direction")]
    [InlineData("receiver")]
    [InlineData("caster")]
    [InlineData("storage")]
    [InlineData("unavailable")]
    [InlineData("mutable")]
    [Theory]
    public void EveryInvalidationDiscardsPartialRows(string change) {
        var views = new SdfIndirectLightViews();
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.Medium);
        var lights = Lights();
        var owner = new object();
        var geometry = default(SdfLightGeometry);
        var receiver = Region;
        SdfLightRegion? caster = Region;
        var mutable = false;

        void Plan(long frame) => views.Plan(casters: caster, forceGeometry: mutable, frame: frame, geometry: geometry, geometryOwner: owner, layout: layout, lights: lights, regions: [receiver, receiver]);
        Plan(frame: 0);
        Assert.False(condition: views.Submitted());
        Plan(frame: 1);
        Assert.Equal(8, views.FirstRow);
        switch (change) {
            case "geometry": geometry = new SdfLightGeometry(Program: 1, Poses: 0, Mesh: 0, Decals: 0); break;
            case "allocation": owner = new object(); break;
            case "owner": lights.ShadowSlots.SetOwner(owner: "replacement", slot: 0); break;
            case "direction": lights.Set(index: 0, light: lights[0] with { Direction = Vector3.UnitX }); break;
            case "receiver": receiver = Region with { Max = new Double3(X: 5, Y: 4, Z: 5) }; break;
            case "caster": caster = Region with { Max = new Double3(X: 6, Y: 5, Z: 6) }; break;
            case "storage": views.InvalidateStorage(); break;
            case "unavailable": caster = null; break;
            case "mutable": mutable = true; break;
        }
        Plan(frame: 2);
        if (change == "unavailable") {
            Assert.Equal(-1, views.Pending);
            caster = Region;
            Plan(frame: 3);
        }
        Assert.Equal(0, views.FirstRow);
        Assert.False(condition: views.Snapshot(index: views.Pending).Valid);
        Assert.False(condition: views.Submitted());
        if (mutable) {
            Plan(frame: 4);
            Assert.Equal(0, views.FirstRow);
            Assert.False(condition: views.Snapshot(index: views.Pending).Valid);
        }
        Assert.Equal(0UL, views.Publications);
    }
    [Fact]
    public void PrimaryDispatchIntersectsItsBoxWithTheValidatedSlice() {
        var source = Source(file: "passes/sdf-cull-args.comp.hlsl");

        Assert.Contains(actualString: source, expectedSubstring: "(passGroup.lightSlice == 0u || slice.x != 0u)");
        Assert.Contains(actualString: source, expectedSubstring: "firstGroup.y = max(firstGroup.y, slice.y);");
        Assert.Contains(actualString: source, expectedSubstring: "endGroup.y = min(endGroup.y, slice.y + slice.z);");
        Assert.Contains(actualString: source, expectedSubstring: "if (any(endGroup <= firstGroup)) { firstGroup = 0u; endGroup = 0u; }");
        Assert.Contains(actualString: source, expectedSubstring: "viewsArgsRW[1] = endGroup.y - firstGroup.y;");
        Assert.Contains(actualString: source, expectedSubstring: "cullBoundsRW[1] = firstGroup.y;");
    }
    [Fact]
    public void DepthCopyTouchesOnlyItsRowsAndPublishesUnresolvedForInvalidInputs() {
        var source = Source(file: "passes/sdf-light-depth.comp.hlsl");

        Assert.Contains(actualString: source, expectedSubstring: "id.y >= slice.z * SdfIndirectLightSliceRowEdge");
        Assert.Contains(actualString: source, expectedSubstring: "id.y + slice.y * SdfIndirectLightSliceRowEdge");
        Assert.Contains(actualString: source, expectedSubstring: "if (address >= depthCount) { return; }");
        Assert.Contains(actualString: source, expectedSubstring: "cullBounds.GetDimensions(boundsCount, boundsStride);");
        Assert.Contains(actualString: source, expectedSubstring: "sdfVisibilityRecordBuffer.GetDimensions(visibilityCount, visibilityStride);");
        Assert.Contains(actualString: source, expectedSubstring: "boundsCount >= 4u && all(passGroup.imageExtent == SdfIndirectLightResolution)");
        Assert.Contains(actualString: source, expectedSubstring: "record <= visibilityCount && SdfVisibilityWords <= visibilityCount - record");
        Assert.Contains(actualString: source, expectedSubstring: "if (!valid) {\n        depth = asfloat(0x7fc00000u);");
        Assert.Contains(actualString: source, expectedSubstring: "indirectLightDepthRW[address] = depth;");
    }

    private static SdfLights Lights() {
        var lights = SdfLights.Default();

        lights.ShadowSlots.SetOwner(owner: "sun", slot: 0);
        return lights;
    }
    private static string Source(string file) => File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: ("src/Puck.SdfVm/Assets/Shaders/Sdf/" + file)));
}
