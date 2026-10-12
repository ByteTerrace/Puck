using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;
using Puck.Testing;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>THE LAW: an inverse board can store every value its codes row can produce, including its empty value;
/// symbolic domains narrow that proof rather than bypassing it. The shipped world tree is enumerated mechanically
/// below so every inverse declaration remains covered as worlds are added.</summary>
public sealed class DerivedBoardDomainLawTests {
    [Fact]
    public void EveryShippedInverseDeclarationPassesTheDomainProof() {
        var files = ShippedWorldDocuments.Files(directory: RepositoryPaths.Resolve(relativePath: ShippedWorldDocuments.WorldDirectory));
        var declarations = 0;

        foreach (var path in files) {
            using var document = JsonDocument.Parse(ShippedWorldDocuments.Read(path: path));

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
                    condition: StateRow.TryProveDerivedDomain(board: board, boardSymbols: boardSymbols, codeSymbols: codeSymbols, codes: code, reason: out var reason),
                    userMessage: $"{path}: {reason}"
                );
            }
        }

        Assert.True(condition: (declarations > 0), userMessage: "the shipped world tree contained no inverse declarations");
    }
}
