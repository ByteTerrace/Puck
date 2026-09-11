using System.Numerics;

using Puck.Commands;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>world.row.set</c>'s literal field form, <c>world.row.add</c>/<c>world.row.remove</c>'s list-element
/// forms, and <c>world.row</c>'s read-back all resolve the same <c>[n]</c>/<c>[field=value]</c> selector grammar
/// <c>world.row.step</c> already used, compose through the SAME section table the whole-row forms use, and share
/// their read-your-writes window guard. Every claim pairs a denied case with a passing control.
/// </summary>
public sealed class WorldRowFieldEditLawTests {
    private sealed class FakeConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }

    private const string PrototypeId = "moth";

    private static ShapeDocument Torso() => new(
        Id: 0,
        Name: "torso",
        Type: SdfSolidPrimitive.Box,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );
    // A Cylinder rather than the brief's own Prism spelling: its rounding ceiling is exactly min(radius, height) at
    // unit scale, so "past the ceiling" and "well inside it" are pinned numbers rather than a trapezoid derivation.
    private static ShapeDocument ForearmL(float? rounding = null) => new(
        Id: 1,
        Name: "forearmL",
        Type: SdfSolidPrimitive.Cylinder,
        Position: new Vector3(x: 0.6f, y: 0f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: Vector3.One,
        Material: 1,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Rounding: rounding
    );
    private static WorldPrototype BuildMoth(IReadOnlyList<ShapeDocument>? shapes = null, IReadOnlyList<PaletteEntryDocument>? palette = null) {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: (palette ?? [
                new PaletteEntryDocument(Color: "#FFFFFF", Emissive: null, Specular: null, Roughness: null),
                new PaletteEntryDocument(Color: "#D8D2C4", Emissive: null, Specular: 0.1f, Roughness: null),
            ]),
            Shapes: (shapes ?? [Torso(), ForearmL()]),
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: PrototypeId
        );

        return new WorldPrototype(
            Document: canonical.Document,
            HashRaw: canonical.Hash,
            Id: PrototypeId
        );
    }
    private static WorldDefinition BuildDocument(WorldPrototype? moth, IReadOnlyList<WorldStateRow>? state) {
        var document = (Fixtures.BuildDocument() with {
            CreationsRaw = [(moth ?? BuildMoth())],
            StateRaw = new WorldStateSection(World: (state ?? [])),
        });

        return (document with {
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = [new WorldPlacement(Id: "mothPlacement", PrototypeId: PrototypeId, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f)],
            }),
        });
    }
    private static HostRow Build(WorldPrototype? moth = null, IReadOnlyList<WorldStateRow>? state = null) => HostRow.Build(
        definition: BuildDocument(
            moth: moth,
            state: state
        ),
        name: "boot"
    );
    private static CommandRegistry BuildRegistry(HostRow row) => new(modules: [
        new WorldRowCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance), echoes: new WorldDeferredVerbEchoes(), link: row.Instance.Link),
    ]);
    private static void Step(HostRow row) => row.Server.Advance(stepTicks: Fixtures.StepTicks);
    private static WorldPrototype MothPrototype(HostRow row) => row.Server.Definition.Creations.Single(predicate: static creation => (creation.Id.Value == PrototypeId));
    private static CreationDocument MothDocument(HostRow row) => MothPrototype(row: row).Document;
    private static ShapeDocument FindShape(HostRow row, string name) => MothDocument(row: row).Shapes!.Single(
        predicate: shape => string.Equals(a: shape.Name?.Value, b: name, comparisonType: StringComparison.Ordinal)
    );

    [Fact]
    public void LiteralSet_ByNameSelector_AppliesAndIsReadBack() {
        using var row = Build();
        var registry = BuildRegistry(row: row);

        var setResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].rounding 0.03");

        Assert.False(condition: setResult.IsError, userMessage: setResult.Output);

        Step(row: row);

        var shape = FindShape(row: row, name: "forearmL");

        Assert.NotNull(@object: shape.Rounding);
        Assert.True(condition: (MathF.Abs(x: (shape.Rounding!.Value - 0.03f)) < 0.0001f));

        var readResult = registry.Submit(line: "world.row creations moth document.shapes[name=forearmL].rounding");

        Assert.False(condition: readResult.IsError, userMessage: readResult.Output);
        Assert.Contains(expectedSubstring: "0.03", actualString: readResult.Output, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void Read_TrailingSelector_ReadsBackTheWholeElement() {
        // A selector at the VERY END of the path (no field after it) addresses the whole array element — the
        // container TryNavigate hands back is the OBJECT the array lives on (e.g. "document"), never the array
        // itself, so the leaf lookup must resolve the array by its own name first.
        using var row = Build();
        var registry = BuildRegistry(row: row);

        var result = registry.Submit(line: "world.row creations moth document.shapes[name=forearmL]");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.Contains(expectedSubstring: "\"name\":\"forearmL\"", actualString: result.Output, comparisonType: StringComparison.Ordinal);
    }
    private static WorldStateRow ArmLift(string text = "[2,3,4]") => new(
        CellName.Parse(candidate: "armLift"),
        CellKind.Text,
        Cells: [new StateCell(Key: WorldStateRow.SlotKey, Text: text)]
    );
    private static void WriteArmLift(HostRow row, string text) => row.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Kind: WorldDocumentWriteKind.Set,
        Key: WorldStateRow.SlotKey.Value,
        Principal: WorldPrincipal.Console,
        Row: "armLift",
        Text: text,
        Value: 0L
    ));

    [Fact]
    public void LiteralSet_StateBindingString_OnACreationShape_RoundTripsAndStaysLive() {
        // The binding is written as a plain JSON string through the literal door; the compose boundary resolves the
        // submitted creation against the current state before canonicalizing it, and the installed shape keeps the
        // reference — a later write to the bound row re-resolves the shape without another document edit.
        using var row = Build(state: [ArmLift()]);
        string? diag = null;

        row.Server.EchoTap = echo => { if (echo.Rejected) { diag = echo.Message; } };

        var registry = BuildRegistry(row: row);
        var setResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].position \"state.armLift\"");

        Assert.False(condition: setResult.IsError, userMessage: setResult.Output);

        Step(row: row);

        Assert.Null(@object: diag);

        var shape = FindShape(row: row, name: "forearmL");

        Assert.Equal(expected: "state.armLift", actual: shape.Position.Reference);
        Assert.Equal(expected: new Vector3(x: 2f, y: 3f, z: 4f), actual: shape.Position.Value);

        var readResult = registry.Submit(line: "world.row creations moth document.shapes[name=forearmL].position");

        Assert.False(condition: readResult.IsError, userMessage: readResult.Output);
        Assert.Contains(expectedSubstring: "\"state.armLift\"", actualString: readResult.Output, comparisonType: StringComparison.Ordinal);

        WriteArmLift(row: row, text: "[5,6,7]");
        Step(row: row);

        Assert.Null(@object: diag);

        var refreshed = FindShape(row: row, name: "forearmL");

        Assert.Equal(expected: "state.armLift", actual: refreshed.Position.Reference);
        Assert.Equal(expected: new Vector3(x: 5f, y: 6f, z: 7f), actual: refreshed.Position.Value);
    }
    [Fact]
    public void LiteralSet_StateBindingString_OnARotation_SurvivesCanonicalization() {
        // The canonicalizer normalizes a literal quaternion; a bound one must pass through with its reference, or
        // every edit to a rig whose poses read state cells would flatten them to the values of the moment.
        var armRot = new WorldStateRow(
            CellName.Parse(candidate: "armRot"),
            CellKind.Text,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Text: "[0,0,0,1]")]
        );
        using var row = Build(state: [armRot]);
        string? diag = null;

        row.Server.EchoTap = echo => { if (echo.Rejected) { diag = echo.Message; } };

        var registry = BuildRegistry(row: row);
        var setResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].rotation \"state.armRot\"");

        Assert.False(condition: setResult.IsError, userMessage: setResult.Output);

        Step(row: row);

        Assert.Null(@object: diag);

        var shape = FindShape(row: row, name: "forearmL");

        Assert.Equal(expected: "state.armRot", actual: shape.Rotation.Reference);
        Assert.Equal(expected: Quaternion.Identity, actual: shape.Rotation.Value);

        // Control: an unrelated edit to the same row re-canonicalizes it and the binding is still there.
        var otherResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].rounding 0.02");

        Assert.False(condition: otherResult.IsError, userMessage: otherResult.Output);

        Step(row: row);

        Assert.Null(@object: diag);
        Assert.Equal(expected: "state.armRot", actual: FindShape(row: row, name: "forearmL").Rotation.Reference);
    }
    [Fact]
    public void BoundRotation_SurvivesEachStageOfTheComposePath() {
        // The three stages an UpsertCreation crosses, each asserted on its own so a dropped reference names its stage.
        var armRot = new WorldStateRow(
            CellName.Parse(candidate: "armRot"),
            CellKind.Text,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Text: "[0,0,0,1]")]
        );
        var bound = System.Text.Json.JsonSerializer.Deserialize<Puck.Assets.Documents.DocumentQuaternion>(json: "\"state.armRot\"", options: Puck.Assets.Documents.DocumentJsonOptions.Shared)!;
        var moth = BuildMoth();
        var submitted = (moth with {
            Document = (moth.Document with { Shapes = [Torso(), (ForearmL() with { Rotation = bound })] }),
            HashRaw = null,
        });
        var definition = BuildDocument(moth: BuildMoth(), state: [armRot]);

        // Stage 1: the row's own JSON round trip plus resolution against the current state.
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value: submitted, jsonTypeInfo: WorldJsonContext.Default.WorldPrototype);
        var copy = System.Text.Json.JsonSerializer.Deserialize(utf8Json: bytes, jsonTypeInfo: WorldJsonContext.Default.WorldPrototype)!;

        Assert.Equal(expected: "state.armRot", actual: copy.Document.Shapes![1].Rotation.Reference);
        Assert.True(condition: WorldStateDocumentValues.TryResolveGraph(source: definition, graph: copy, reason: out var resolveReason), userMessage: resolveReason);
        Assert.Equal(expected: "state.armRot", actual: copy.Document.Shapes![1].Rotation.Reference);

        // Stage 2: canonicalization keeps the reference (it normalizes a literal quaternion only).
        var canonical = CreationCanonicalizer.Canonicalize(document: copy.Document, source: PrototypeId);

        Assert.Equal(expected: "state.armRot", actual: canonical.Document.Shapes![1].Rotation.Reference);

        // Stage 3: the whole-candidate rehydration keeps it too.
        var candidate = (definition with { CreationsRaw = [(copy with { Document = canonical.Document, HashRaw = canonical.Hash })] });

        Assert.True(condition: WorldStateDocumentValues.TryRehydrate(definition: candidate, refreshed: out var refreshed, reason: out var rehydrateReason), userMessage: rehydrateReason);
        Assert.Equal(expected: "state.armRot", actual: refreshed.Creations.Single().Document.Shapes![1].Rotation.Reference);
    }
    [Fact]
    public void LiteralSet_StateBindingString_OnAPlacement_RoundTrips() {
        using var row = Build(state: [ArmLift()]);
        string? diag = null;

        row.Server.EchoTap = echo => { if (echo.Rejected) { diag = echo.Message; } };

        var registry = BuildRegistry(row: row);
        var setResult = registry.Submit(line: "world.row.set placements mothPlacement position \"state.armLift\"");

        Assert.False(condition: setResult.IsError, userMessage: setResult.Output);

        Step(row: row);

        Assert.Null(@object: diag);

        var placement = row.Server.Definition.Placements.Single(predicate: static p => (p.Id == "mothPlacement"));

        Assert.Equal(expected: "state.armLift", actual: placement.Position.Reference);
        Assert.Equal(expected: new Vector3(x: 2f, y: 3f, z: 4f), actual: placement.Position.Value);

        // Control: the identical field addressed with a plain literal drops the reference.
        var controlResult = registry.Submit(line: "world.row.set placements mothPlacement position [1,2,3]");

        Assert.False(condition: controlResult.IsError, userMessage: controlResult.Output);

        Step(row: row);

        var literal = row.Server.Definition.Placements.Single(predicate: static p => (p.Id == "mothPlacement"));

        Assert.Null(@object: literal.Position.Reference);
        Assert.Equal(expected: new Vector3(x: 1f, y: 2f, z: 3f), actual: literal.Position.Value);
    }
    [Fact]
    public void LiteralSet_StateBindingNamingNoCell_RefusesByNameAndLeavesRowUnchanged() {
        using var row = Build(state: [ArmLift()]);
        string? diag = null;

        row.Server.EchoTap = echo => { if (echo.Rejected) { diag = echo.Message; } };

        var registry = BuildRegistry(row: row);
        var before = MothDocument(row: row);
        var setResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].position \"state.nowhere\"");

        Assert.False(condition: setResult.IsError, userMessage: setResult.Output);

        Step(row: row);

        Assert.NotNull(@object: diag);
        Assert.Contains(expectedSubstring: "state.nowhere", actualString: diag, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "must name a declared state cell", actualString: diag, comparisonType: StringComparison.Ordinal);
        Assert.Same(expected: before, actual: MothDocument(row: row));
    }
    [Fact]
    public void LiteralSet_DottedSelectorValue_ResolvesInsideTheBracket() {
        // The path splits on '.' outside brackets only, so a fractional selector value is one segment.
        using var row = Build();
        var registry = BuildRegistry(row: row);

        var badResult = registry.Submit(line: "world.row.set creations moth document.palette[specular=0.5].color \"#010203\"");

        Assert.True(condition: badResult.IsError);
        Assert.Contains(expectedSubstring: "no element of 'palette' has specular=0.5", actualString: badResult.Output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "0.1", actualString: badResult.Output, comparisonType: StringComparison.Ordinal);

        var goodResult = registry.Submit(line: "world.row.set creations moth document.palette[specular=0.1].color \"#010203\"");

        Assert.False(condition: goodResult.IsError, userMessage: goodResult.Output);

        Step(row: row);

        Assert.Equal(expected: "#010203", actual: MothDocument(row: row).Palette![1].Color);
    }
    [Fact]
    public void LiteralSet_ThenStep_SameRowSameWindow_RefusesByNameThenAppliesNextWindow() {
        using var row = Build();
        var registry = BuildRegistry(row: row);

        var setResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].rounding 0.02");

        Assert.False(condition: setResult.IsError, userMessage: setResult.Output);

        var collided = registry.Submit(line: "world.row.step creations.moth.document.shapes[name=forearmL].rounding 0.01");

        Assert.True(condition: collided.IsError);
        Assert.Contains(expectedSubstring: "row 'creations.moth' already has a step buffered this tick", actualString: collided.Output, comparisonType: StringComparison.Ordinal);

        Step(row: row);

        var stepResult = registry.Submit(line: "world.row.step creations.moth.document.shapes[name=forearmL].rounding 0.01");

        Assert.False(condition: stepResult.IsError, userMessage: stepResult.Output);

        Step(row: row);

        var shape = FindShape(row: row, name: "forearmL");

        Assert.True(condition: (MathF.Abs(x: (shape.Rounding!.Value - 0.03f)) < 0.0001f));
    }
    [Fact]
    public void LiteralSet_ThenAddOrRemove_SameRowSameWindow_RefusesByNameThenAppliesNextWindow() {
        using var row = Build();
        var registry = BuildRegistry(row: row);

        var setResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].rounding 0.02");

        Assert.False(condition: setResult.IsError, userMessage: setResult.Output);

        var addCollided = registry.Submit(line: """world.row.add creations moth document.palette {"color":"#112233"}""");

        Assert.True(condition: addCollided.IsError);
        Assert.Contains(expectedSubstring: "row 'creations.moth' already has an edit buffered this tick", actualString: addCollided.Output, comparisonType: StringComparison.Ordinal);

        var removeCollided = registry.Submit(line: "world.row.remove creations moth document.shapes name=torso");

        Assert.True(condition: removeCollided.IsError);
        Assert.Contains(expectedSubstring: "row 'creations.moth' already has an edit buffered this tick", actualString: removeCollided.Output, comparisonType: StringComparison.Ordinal);

        Step(row: row);

        var addResult = registry.Submit(line: """world.row.add creations moth document.palette {"color":"#112233"}""");

        Assert.False(condition: addResult.IsError, userMessage: addResult.Output);

        Step(row: row);

        var removeResult = registry.Submit(line: "world.row.remove creations moth document.shapes name=torso");

        Assert.False(condition: removeResult.IsError, userMessage: removeResult.Output);

        Step(row: row);

        var document = MothDocument(row: row);

        Assert.Equal(expected: 3, actual: document.Palette!.Count);
        Assert.Single(collection: document.Shapes!);
        Assert.True(condition: (MathF.Abs(x: (document.Shapes![0].Rounding!.Value - 0.02f)) < 0.0001f));
    }
    [Fact]
    public void Read_BareListField_ListsOneLinePerElement() {
        using var row = Build();
        var registry = BuildRegistry(row: row);

        var result = registry.Submit(line: "world.row creations moth document.shapes");

        Assert.False(condition: result.IsError, userMessage: result.Output);

        var lines = result.Output.Split(separator: Environment.NewLine);

        Assert.Equal(expected: 3, actual: lines.Length);
        Assert.Equal(expected: "[world.row: creations moth.document.shapes: 2 element(s)]", actual: lines[0]);
        Assert.StartsWith(expectedStartString: "[world.row 0: \"torso\" {", actualString: lines[1], comparisonType: StringComparison.Ordinal);
        Assert.StartsWith(expectedStartString: "[world.row 1: \"forearmL\" {", actualString: lines[2], comparisonType: StringComparison.Ordinal);
        Assert.EndsWith(expectedEndString: "}]", actualString: lines[2], comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void Read_WholeRow_OmitsTheHashAndIsAcceptedBackByTheWholeRowSet() {
        using var row = Build();
        string? diag = null;

        row.Server.EchoTap = echo => { if (echo.Rejected) { diag = echo.Message; } };

        var registry = BuildRegistry(row: row);
        var result = registry.Submit(line: "world.row creations moth");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.StartsWith(expectedStartString: "[world.row: creations moth = {", actualString: result.Output, comparisonType: StringComparison.Ordinal);
        Assert.DoesNotContain(expectedSubstring: "\"hash\"", actualString: result.Output, comparisonType: StringComparison.Ordinal);

        // The digest stays readable by field path.
        var hashResult = registry.Submit(line: "world.row creations moth hash");

        Assert.False(condition: hashResult.IsError, userMessage: hashResult.Output);
        Assert.Contains(expectedSubstring: $"\"{MothPrototype(row: row).Hash}\"", actualString: hashResult.Output, comparisonType: StringComparison.Ordinal);

        var echoed = System.Text.Json.Nodes.JsonNode.Parse(json: result.Output[(result.Output.IndexOf(value: " = ", comparisonType: StringComparison.Ordinal) + 3)..^1])!.AsObject();

        echoed["document"]!["shapes"]![1]!["rounding"] = 0.04;

        var setResult = registry.Submit(line: $"world.row.set creations {echoed.ToJsonString()}");

        Assert.False(condition: setResult.IsError, userMessage: setResult.Output);

        Step(row: row);

        Assert.Null(@object: diag);
        Assert.True(condition: (MathF.Abs(x: (FindShape(row: row, name: "forearmL").Rounding!.Value - 0.04f)) < 0.0001f));

        // Control: the same edited row carrying its pre-edit digest is refused at compose by the hash check.
        echoed["document"]!["shapes"]![1]!["rounding"] = 0.05;
        echoed["hash"] = MothPrototype(row: row).Hash;

        var staleResult = registry.Submit(line: $"world.row.set creations {echoed.ToJsonString()}");

        Assert.False(condition: staleResult.IsError, userMessage: staleResult.Output);

        Step(row: row);

        Assert.NotNull(@object: diag);
        Assert.Contains(expectedSubstring: "does not match the canonical sha256", actualString: diag, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: (MathF.Abs(x: (FindShape(row: row, name: "forearmL").Rounding!.Value - 0.04f)) < 0.0001f));
    }
    [Fact]
    public void LiteralSet_UnknownSelector_RefusesByNameListingCandidates() {
        using var row = Build();
        var registry = BuildRegistry(row: row);

        var badResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=bogus].rounding 0.02");

        Assert.True(condition: badResult.IsError);
        Assert.Contains(expectedSubstring: "no element of 'shapes' has name=bogus", actualString: badResult.Output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "torso", actualString: badResult.Output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "forearmL", actualString: badResult.Output, comparisonType: StringComparison.Ordinal);

        // Control: the identical field addressed by its genuine name steps cleanly.
        var goodResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].rounding 0.02");

        Assert.False(condition: goodResult.IsError, userMessage: goodResult.Output);
    }
    [Fact]
    public void LiteralSet_AmbiguousSelector_RefusesByName() {
        var duplicated = BuildMoth(shapes: [Torso(), ForearmL(), (ForearmL() with { Id = 2 })]);

        using var row = Build(moth: duplicated);
        var registry = BuildRegistry(row: row);

        var result = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].rounding 0.02");

        Assert.True(condition: result.IsError);
        Assert.Contains(expectedSubstring: "ambiguous", actualString: result.Output, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void Add_AppendsByDefaultAndInsertsAfterASelector() {
        using var row = Build();
        string? diag = null;
        row.Server.EchoTap = echo => { if (echo.Rejected) { diag = echo.Message; } };
        var registry = BuildRegistry(row: row);

        var appendResult = registry.Submit(line: """world.row.add creations moth document.palette {"color":"#112233"}""");

        Assert.False(condition: appendResult.IsError, userMessage: appendResult.Output);

        Step(row: row);

        var palette = MothDocument(row: row).Palette!;

        Assert.True(condition: (palette.Count == 3), userMessage: diag);
        Assert.Equal(expected: "#112233", actual: palette[2].Color);

        var afterResult = registry.Submit(line: """world.row.add creations moth document.shapes {"id":9,"type":"Sphere","position":[0,0,0],"rotation":[0,0,0,1],"scale":[0.2,0.2,0.2]} after=name=torso""");

        Assert.False(condition: afterResult.IsError, userMessage: afterResult.Output);

        Step(row: row);

        var shapes = MothDocument(row: row).Shapes!;

        Assert.True(condition: (shapes.Count == 3), userMessage: diag);
        Assert.Equal(expected: SdfSolidPrimitive.Sphere, actual: shapes[1].Type);
        Assert.Equal(expected: "torso", actual: shapes[0].Name?.Value);
        Assert.Equal(expected: "forearmL", actual: shapes[2].Name?.Value);
    }
    [Fact]
    public void Remove_DeletesOneListElementBySelector() {
        using var row = Build();
        var registry = BuildRegistry(row: row);

        var result = registry.Submit(line: "world.row.remove creations moth document.shapes name=torso");

        Assert.False(condition: result.IsError, userMessage: result.Output);

        Step(row: row);

        var shapes = MothDocument(row: row).Shapes!;

        Assert.Single(collection: shapes);
        Assert.Equal(expected: "forearmL", actual: shapes[0].Name?.Value);
    }
    [Fact]
    public void LiteralSet_ValidationFailure_RefusesWithValidatorTextAndLeavesRowUnchanged() {
        using var row = Build();
        string? rejectedMessage = null;

        row.Server.EchoTap = echo => {
            if (echo.Rejected) {
                rejectedMessage = echo.Message;
            }
        };

        var registry = BuildRegistry(row: row);
        var before = MothDocument(row: row);

        // 5 world units is well past this Cylinder's rounding ceiling (min(radius, height) = 1 at its authored
        // unit scale) — the SdfSolidGeometry validator's own refusal, not this door's.
        var setResult = registry.Submit(line: "world.row.set creations moth document.shapes[name=forearmL].rounding 5");

        Assert.False(condition: setResult.IsError, userMessage: setResult.Output);

        Step(row: row);

        Assert.NotNull(@object: rejectedMessage);
        Assert.Contains(expectedSubstring: "edge-rounding radius", actualString: rejectedMessage, comparisonType: StringComparison.Ordinal);
        // The candidate never swapped in: the live document is the SAME object the pre-submit read saw.
        Assert.Same(expected: before, actual: MothDocument(row: row));
    }
    [Fact]
    public void RowStep_SameSelectorGrammar_StepsARoundingField() {
        // world.row.step's delta needs an EXISTING leaf value to add to (world.row.set's literal form does not — see
        // WorldRowFieldPath.TrySetLeaf's remarks) — the shape authors a starting rounding rather than the null every
        // other law in this file starts from.
        using var row = Build(moth: BuildMoth(shapes: [Torso(), ForearmL(rounding: 0.01f)]));
        var registry = BuildRegistry(row: row);

        var result = registry.Submit(line: "world.row.step creations.moth.document.shapes[name=forearmL].rounding 0.04");

        Assert.False(condition: result.IsError, userMessage: result.Output);

        Step(row: row);

        var shape = FindShape(row: row, name: "forearmL");

        Assert.NotNull(@object: shape.Rounding);
        Assert.True(condition: (MathF.Abs(x: (shape.Rounding!.Value - 0.05f)) < 0.0001f));
    }
}
