using System.Numerics;

using Puck.Commands;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// <c>world.slice</c>'s parse and its file, on the CPU: the explicit form names the plane and square, the placement form
/// centres the square on a placement's position and sizes it from its creation's reach at its scale, the options override
/// the band spacing and resolution, every malformed line is refused by name, the writer reports the slice's statistics and
/// path or the failure to write, and a host with no renderer refuses the verb.
/// </summary>
public sealed class WorldSliceCommandModuleLawTests {
    private static readonly Vector3 CubePosition = new(x: 2f, y: 1f, z: 3f);

    private static readonly ShapeDocument Cube = new(Id: 0, Name: "cube", Type: SdfSolidPrimitive.Box, Position: Vector3.Zero, Rotation: Quaternion.Identity,
        Scale: new Vector3(value: 0.5f), Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0);

    private static WorldDefinition Definition(params ShapeDocument[] shapes) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: "cube",
                Palette: [new PaletteEntryDocument(Color: "#FFFFFF", Emissive: null, Specular: null, Roughness: null)],
                Shapes: ((shapes.Length > 0) ? shapes : [Cube]),
                Frames: null
            ),
            source: "cube"
        );
        var document = (Fixtures.BuildDocument() with {
            CreationsRaw = [new WorldPrototype(Document: canonical.Document, HashRaw: canonical.Hash, Id: "cube")],
        });

        return (document with {
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = [new WorldPlacement(Id: "cube-a", PrototypeId: "cube", Position: CubePosition, YawDegrees: 0f, Scale: 2f)],
            }),
        });
    }
    private static WorldSliceRequest Parse(params string[] args) {
        Assert.True(condition: WorldSliceCommandModule.TryParse(args: args, definition: Definition(), refusal: out var refusal, request: out var request), userMessage: refusal);
        return request;
    }

    [Fact]
    public void TheExplicitFormNamesThePlaneAndTheSquare() {
        var request = Parse("Y", "0.5", "-1", "2", "1.5", "slice.png");

        Assert.Equal(expected: new WorldSliceRequest(Axis: SdfAxis.Y, Bands: 0.15f, Centre: new Vector2(x: -1f, y: 2f), Half: 1.5f, Offset: 0.5f,
            Path: Path.GetFullPath(path: "slice.png"), Resolution: WorldSliceRequest.DefaultResolution), actual: request);
        Assert.Equal(expected: (SdfAxis.X, 0.02f, 64), actual: (Parse("x", "0", "0", "0", "1", "s.png", "bands", "0.02", "res", "64") is var options
            ? (options.Axis, options.Bands, options.Resolution) : default));
    }
    [Fact]
    public void ThePlacementFormCentresOnThePlacementAndSizesItFromItsReach() {
        var definition = Definition();
        var reach = CreationGeometry.Reach(document: definition.Creations[0].EngineDocument);
        var request = Parse("cube-a", "z", "cube.png", "offset", "0.25", "res", "32");

        Assert.Equal(expected: SdfAxis.Z, actual: request.Axis);
        Assert.Equal(expected: (CubePosition.Z + 0.25f), actual: request.Offset);
        Assert.Equal(expected: new Vector2(x: CubePosition.X, y: CubePosition.Y), actual: request.Centre);
        Assert.Equal(expected: ((WorldSliceRequest.PlacementMargin * reach) * 2f), actual: request.Half, precision: 5);
        Assert.Equal(expected: 32, actual: request.Resolution);
        Assert.Equal(expected: new Vector2(x: CubePosition.Z, y: CubePosition.Y), actual: Parse("cube-a", "x", "c.png").Centre);
        Assert.Equal(expected: new Vector2(x: CubePosition.X, y: CubePosition.Z), actual: Parse("cube-a", "y", "c.png").Centre);
        Assert.Equal(expected: CubePosition.Y, actual: Parse("cube-a", "y", "c.png").Offset);
    }
    [Fact]
    public void AnUnboundedLatticeFramesItsClipOrIsRefused() {
        var lattice = (Cube with { Name = "lattice", Domain = [new ShapeDomainOp.Repeat(Spacing: new Vector3(value: 2f))] });
        var clip = (Cube with { Id = 1, Name = "clip", Blend = SdfBlendOp.Intersection, Position = new Vector3(x: 0f, y: 1f, z: 0f) });
        var clipped = WorldSliceCommandModule.TryParse(args: ["cube-a", "y", "c.png"], definition: Definition(lattice, clip), refusal: out var refusal, request: out var request);

        Assert.True(condition: clipped, userMessage: refusal);
        Assert.True(condition: (float.IsFinite(f: request.Half) && (request.Half > 0f)), userMessage: $"{request.Half}");
        Assert.False(condition: WorldSliceCommandModule.TryParse(args: ["cube-a", "y", "c.png"], definition: Definition(lattice), refusal: out refusal, request: out _));
        Assert.Contains(expectedSubstring: "placement 'cube-a' has no finite bound", actualString: refusal, comparisonType: StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("expected <x|y|z> <offset>", "y", "0")]
    [InlineData("finite numbers and a positive half", "y", "0", "0", "0", "-1", "s.png")]
    [InlineData("finite numbers and a positive half", "y", "zero", "0", "0", "1", "s.png")]
    [InlineData("'nope' is neither an axis (x|y|z) nor a placement — placements: cube-a", "nope", "y", "s.png")]
    [InlineData("expected an axis (x|y|z) after placement 'cube-a', got 'w'", "cube-a", "w", "s.png")]
    [InlineData("offset expects a finite number of metres, got 'up'", "cube-a", "y", "s.png", "offset", "up")]
    [InlineData("'bands' expects a value", "cube-a", "y", "s.png", "bands")]
    [InlineData("cannot read 'res 9'", "cube-a", "y", "s.png", "res", "9")]
    [InlineData("cannot read 'bands -1'", "cube-a", "y", "s.png", "bands", "-1")]
    [InlineData("cannot read 'size 3'", "cube-a", "y", "s.png", "size", "3")]
    [InlineData("the path must name a .png file, got 's.jpg'", "cube-a", "y", "s.jpg")]
    public void AMalformedLineIsRefusedByName(string refusal, params string[] args) {
        Assert.False(condition: WorldSliceCommandModule.TryParse(args: args, definition: Definition(), refusal: out var actual, request: out _));
        Assert.Contains(expectedSubstring: refusal, actualString: actual, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void TheWriterReportsTheSliceAndItsPathOrTheFailure() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-world-slice-");

        try {
            var slice = new SdfFieldSlice(axis: SdfAxis.Y, bands: 0.1f, centre: Vector2.Zero, half: 1f, offset: 0f, resolution: 16);
            var path = Path.Combine(path1: directory.FullName, path2: "nested", path3: "slice.png");

            Assert.Equal(expected: $"[world.slice] {slice.Describe()} -> {path}", actual: WorldSliceCommandModule.Write(path: path, slice: slice));
            Assert.True(condition: File.Exists(path: path));
            // A path under a file cannot be written.
            Assert.StartsWith(expectedStartString: "[world.slice] failed to write ", actualString: WorldSliceCommandModule.Write(path: Path.Combine(path1: path, path2: "under.png"), slice: slice), comparisonType: StringComparison.Ordinal);
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void AHostWithNoRendererRefusesTheVerb() {
        using var row = HostRow.Build(definition: Definition(), name: "slice");
        var registry = new CommandRegistry(modules: [new WorldSliceCommandModule(renderProbe: new WorldRenderProbe(), server: row.Server, source: new WorldDefinitionSource(Definition: row.Server.Definition, SourcePath: "worlds/test/test.puck"))]);
        var refused = registry.Submit(line: "world.slice cube-a y cube.png");
        var malformed = registry.Submit(line: "world.slice");

        Assert.True(condition: refused.IsError, userMessage: refused.Output);
        Assert.StartsWith(expectedStartString: "[world.slice: no renderer", actualString: refused.Output, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: malformed.IsError, userMessage: malformed.Output);
        Assert.StartsWith(expectedStartString: "[world.slice: expected", actualString: malformed.Output, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void TheSaveFormNamesItsOwnPathAndRefusesWhatItCannotName() {
        using var row = HostRow.Build(definition: Definition(), name: "slice-save");
        var registry = new CommandRegistry(modules: [new WorldSliceCommandModule(renderProbe: new WorldRenderProbe(), server: row.Server, source: new WorldDefinitionSource(Definition: row.Server.Definition, SourcePath: "worlds/test/test.puck"))]);
        var parsed = WorldSliceCommandModule.TryParse(args: ["cube-a", "y", "slice.png", "offset", "1.2"], definition: row.Server.Definition, refusal: out var refusal, request: out var request);

        Assert.True(condition: parsed, userMessage: refusal);
        Assert.StartsWith(expectedStartString: "[world.slice: no renderer", actualString: registry.Submit(line: "world.slice.save cube-a y offset 1.2").Output, comparisonType: StringComparison.Ordinal);
        Assert.StartsWith(expectedStartString: "[world.slice.save: expected", actualString: registry.Submit(line: "world.slice.save cube-a").Output, comparisonType: StringComparison.Ordinal);
        Assert.StartsWith(expectedStartString: "[world.slice.save: placement", actualString: registry.Submit(line: "world.slice.save .. y").Output, comparisonType: StringComparison.Ordinal);
        Assert.StartsWith(expectedStartString: "[world.slice: '", actualString: registry.Submit(line: "world.slice.save missing y").Output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: (request.Offset - 1.2f), actual: WorldSliceCommandModule.TryParse(args: ["cube-a", "y", "slice.png"], definition: row.Server.Definition, refusal: out _, request: out var flat) ? flat.Offset : float.NaN, tolerance: 1e-5f);
    }
    [Fact]
    public void NearNamesThePlacementOfTheSpawnPointNearestTheSeat() {
        var definition = Definition();

        using var row = HostRow.Build(definition: (definition with {
            SpawnPointsRaw = [.. definition.SpawnPoints, new WorldSpawnPoint(Id: "cube-a", Position: (CubePosition - new Vector3(x: 0f, y: 1f, z: 2.8f))), new WorldSpawnPoint(Id: "unplaced", Position: Vector3.Zero)],
        }), name: "slice-near");
        var registry = new CommandRegistry(modules: [new WorldSliceCommandModule(renderProbe: new WorldRenderProbe(), server: row.Server, source: new WorldDefinitionSource(Definition: row.Server.Definition, SourcePath: "worlds/test/test.puck"))]);

        // No seat body yet: nothing stands to be near.
        Assert.StartsWith(expectedStartString: "[world.slice.save: 'near' names", actualString: registry.Submit(line: "world.slice.save near y").Output, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: row.Server.ApplySession(request: new Puck.World.Protocol.SessionRequest.Join(
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            IdentityName: null,
            WireProtocolKey: Puck.World.Protocol.WorldProtocol.WireProtocolKey
        )).Accepted);
        row.Server.Body(index: 0)!.Pose(x: 2f, y: 0f, z: 0.5f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
        // The nearest spawn point with a placement resolves, so the line reaches the renderer check.
        Assert.StartsWith(expectedStartString: "[world.slice: no renderer", actualString: registry.Submit(line: "world.slice.save near y offset 1.2").Output, comparisonType: StringComparison.Ordinal);
    }
}
