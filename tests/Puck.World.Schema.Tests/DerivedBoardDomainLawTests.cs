using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>THE LAW: an inverse board can store every value its codes row can produce, including its empty value;
/// symbolic domains narrow that proof rather than bypassing it. The shipped world tree is enumerated mechanically
/// below so every inverse declaration remains covered as worlds are added.</summary>
public sealed class DerivedBoardDomainLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    [Fact]
    public void CodeRangeMustBeContainedByBoardRange() {
        var board = new StateRow(
            Name: Name(value: "board"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 1L,
            Domain: new StateDomain.CellsOf(Topology: "grid")
        );
        var codes = new StateRow(
            Name: Name(value: "codes"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 2L,
            Domain: new StateDomain.Keys()
        );

        Assert.False(StateRow.TryProveDerivedDomain(board, null, codes, null, out var reason));
        Assert.Contains("can admit 0..2", reason, StringComparison.Ordinal);

        var unboundedBoard = board with { Min = null, Max = null };
        var unboundedCodes = codes with { Min = null, Max = null };

        Assert.True(StateRow.TryProveDerivedDomain(unboundedBoard, null, unboundedCodes, null, out reason), reason);

        var boolBoard = unboundedBoard with { Kind = CellKind.Bool };
        var boolCodes = unboundedCodes with { Min = 0L, Max = 1L };

        Assert.True(StateRow.TryProveDerivedDomain(boolBoard, null, boolCodes, null, out reason), reason);
        Assert.False(StateRow.TryProveDerivedDomain(boolBoard, null, unboundedCodes, null, out reason));
        Assert.Contains("can admit", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void EmptyAndEnumIntersectionsAreAdmittedAsTheirEffectiveDomains() {
        var board = new StateRow(
            Name: Name(value: "board"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 2L,
            Enum: Name(value: "boardValues"),
            Domain: new StateDomain.CellsOf(Topology: "grid")
        );
        var codes = new StateRow(
            Name: Name(value: "codes"),
            Kind: CellKind.Int,
            Min: -4L,
            Max: 5L,
            Enum: Name(value: "codeValues"),
            Domain: new StateDomain.Keys()
        );
        var boardValues = new StateEnum(Name(value: "boardValues"), [Name(value: "empty"), Name(value: "piece")]);
        var codeValues = new StateEnum(Name(value: "codeValues"), [Name(value: "piece"), Name(value: "other")]);

        Assert.True(StateRow.TryProveDerivedDomain(board, boardValues, codes, codeValues, out var reason), reason);

        var boardWithOneValue = board with { Enum = Name(value: "boardOne") };
        var boardOne = new StateEnum(Name(value: "boardOne"), [Name(value: "empty")]);
        var twoCodeValues = codes with { Min = 0L, Max = 1L };

        Assert.False(StateRow.TryProveDerivedDomain(boardWithOneValue, boardOne, twoCodeValues, codeValues, out reason));
        Assert.Contains("admits 0..0", reason, StringComparison.Ordinal);

        var emptyOutside = board with { Min = 0L, Max = 1L };
        var noEmpty = emptyOutside with { Domain = new StateDomain.CellsOf(Empty: 2L, Topology: "grid") };

        Assert.False(StateRow.TryProveDerivedDomain(noEmpty, boardValues, codes with { Min = 0L, Max = 1L }, codeValues, out reason));
        Assert.Contains("empty value 2", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void EveryShippedInverseDeclarationPassesTheDomainProof() {
        var root = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((root is not null) && !Directory.Exists(path: Path.Combine(root.FullName, "src", "Puck.World", "Assets", "worlds"))) {
            root = root.Parent;
        }
        Assert.NotNull(@object: root);

        var files = Directory.EnumerateFiles(
            Path.Combine(root!.FullName, "src", "Puck.World", "Assets", "worlds"),
            "*.world.json",
            SearchOption.AllDirectories
        ).OrderBy(path => path, StringComparer.Ordinal);
        var declarations = 0;

        foreach (var path in files) {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path: path));

            if (!document.RootElement.TryGetProperty(propertyName: "state", value: out var stateJson)) {
                continue;
            }

            var rawState = JsonNode.Parse(stateJson.GetRawText())!.AsObject();
            var rawWorld = rawState["world"]!.AsArray();
            var needed = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var rawRow in rawWorld.OfType<JsonObject>()) {
                if (rawRow["inverse"] is JsonObject inverse) {
                    needed.Add(item: rawRow["name"]!.GetValue<string>());
                    needed.Add(item: inverse["codes"]!.GetValue<string>());
                }
            }
            var filteredWorld = new JsonArray();

            foreach (var rawRow in rawWorld.OfType<JsonObject>()) {
                if (!needed.Contains(item: rawRow["name"]!.GetValue<string>())) {
                    continue;
                }

                var row = rawRow.DeepClone().AsObject();

                row.Remove(propertyName: "cells");
                filteredWorld.Add(value: row);
            }
            rawState["world"] = filteredWorld;
            var state = JsonSerializer.Deserialize(
                json: rawState.ToJsonString(),
                jsonTypeInfo: ((JsonTypeInfo<WorldStateSection>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(WorldStateSection))!)
            )!;
            var definition = new WorldDefinition(StateRaw: state);

            foreach (var board in (state.World ?? [])) {
                if (board.Inverse is null) {
                    continue;
                }

                declarations++;
                var code = WorldDefinitionRows.FindStateRow(state.World, board.Inverse.Codes);

                Assert.NotNull(@object: code);
                var catalog = definition.StateCatalog;

                Assert.True(condition: catalog.TryResolve(StateLane.Document, board.Name, out var boardHandle));
                Assert.True(condition: catalog.TryResolve(StateLane.Document, code!.Name, out var codeHandle));
                _ = catalog.TryGetEnum(handle: boardHandle, symbols: out var boardSymbols);
                _ = catalog.TryGetEnum(handle: codeHandle, symbols: out var codeSymbols);

                Assert.True(
                    StateRow.TryProveDerivedDomain(board, boardSymbols, code, codeSymbols, out var reason),
                    $"{path}: {reason}"
                );
            }
        }

        Assert.True(condition: (declarations > 0), userMessage: "the shipped world tree contained no inverse declarations");
    }
}
