using Puck.Commands;
using System.Text.Json;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseMetadataTransitionLawTests {
    private static void AssertMetadata(WorldAuthorityCheckpoint checkpoint, string title, string description) {
        foreach (var bytes in new[] { checkpoint.Server.DefinitionJson, checkpoint.Server.BaseDefinitionJson }) {
            var metadata = WorldDefinitionSerialization.Deserialize(utf8Json: bytes).Metadata!;

            Assert.Equal(
                title,
                metadata.Title
            );
            Assert.Equal(
                description,
                metadata.Description
            );
        }
    }
    private static void AssertOnlyDefinitionsChanged(WorldAuthorityCheckpoint before, WorldAuthorityCheckpoint after) {
        foreach (var pair in new[] { (before.Server.DefinitionJson, after.Server.DefinitionJson), (before.Server.BaseDefinitionJson, after.Server.BaseDefinitionJson) }) {
            var original = WorldDefinitionSerialization.Deserialize(utf8Json: pair.Item1) with { Metadata = null };
            var changed = WorldDefinitionSerialization.Deserialize(utf8Json: pair.Item2) with { Metadata = null };

            Assert.Equal(
                WorldDefinitionSerialization.Serialize(definition: original),
                WorldDefinitionSerialization.Serialize(definition: changed)
            );
        }
        Assert.Equal(
            WorldAuthorityCheckpointCodec.Encode(checkpoint: before),
            WorldAuthorityCheckpointCodec.Encode(checkpoint: after with {
                Server = after.Server with {
                    DefinitionJson = before.Server.DefinitionJson,
                    BaseDefinitionJson = before.Server.BaseDefinitionJson,
                },
            })
        );
    }
    private static Dictionary<string, JsonElement> Bag(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private static WorldAuthorityCheckpoint Capture(WorldFixture fixture) {
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                WorldAuthorityHostRowCheckpoint.Empty,
                out var checkpoint,
                out var reason
            ),
            userMessage: reason
        );
        return checkpoint!;
    }

    [Fact]
    public void AddingAndRemovingMetadataPreservesUnrelatedLiveMembers() {
        var a = Fixtures.BuildDocument() with { Metadata = null };
        var b = a with { Metadata = new(Title: "published") };
        using var fixture = Fixtures.FreshServer(a with { Metadata = new(Description: "live note") });
        var original = Capture(fixture: fixture);

        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                a,
                b,
                original,
                out var upgraded,
                out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                b,
                a,
                upgraded!,
                out var reversed,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            WorldAuthorityCheckpointCodec.Encode(checkpoint: original),
            WorldAuthorityCheckpointCodec.Encode(checkpoint: reversed!)
        );
    }
    [InlineData(false, "player edit")]
    [InlineData(true, "player edit")]
    [InlineData(false, "B")]
    [Theory]
    public void ConflictsInEitherLiveDefinitionOrUndoBaseRefuseWithoutChangingAnyBytes(bool undoBase, string title) {
        var a = Fixtures.BuildDocument() with { Metadata = new(Title: "A") };
        var b = a with { Metadata = new(Title: "B") };
        using var fixture = Fixtures.FreshServer(a);
        var current = Capture(fixture: fixture);
        var conflict = WorldDefinitionSerialization.Serialize(definition: a with { Metadata = new(Title: title) });

        current = current with {
            Server = (undoBase
            ? current.Server with { BaseDefinitionJson = conflict }
            : current.Server with { DefinitionJson = conflict }),
        };
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: current);

        Assert.False(condition: WorldReleaseMetadataTransition.TryApply(
            a,
            b,
            current,
            out var result,
            out var reason
        ));
        Assert.Null(@object: result);
        Assert.Contains(
            actualString: reason,
            expectedSubstring: (undoBase
            ? "undo base/metadata/title"
            : "live/metadata/title")
        );
        Assert.Equal(
            bytes,
            WorldAuthorityCheckpointCodec.Encode(checkpoint: current)
        );
    }
    [Fact]
    public void CustomMembersMergeIndependentlyAndArraysRemainIndivisible() {
        var a = Fixtures.BuildDocument() with {
            Metadata = new(
            Tags: ["old"],
            Custom: Bag(json: "{\"nested\":{\"removed\":1,\"changed\":2}}")
        ),
        };
        var b = a with {
            Metadata = new(
            Tags: ["new"],
            Custom: Bag(json: "{\"nested\":{\"added\":3,\"changed\":4}}")
        ),
        };
        using var fixture = Fixtures.FreshServer(a with {
            Metadata = a.Metadata! with {
                Custom = Bag(json: "{\"nested\":{\"removed\":1,\"changed\":2,\"player\":5},\"unrelated\":true}"),
            },
        });
        var original = Capture(fixture: fixture);

        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                a,
                b,
                original,
                out var upgraded,
                out var reason
            ),
            userMessage: reason
        );
        var metadata = WorldDefinitionSerialization.Deserialize(utf8Json: upgraded!.Server.DefinitionJson).Metadata!;

        Assert.Equal(
            5,
            metadata.Custom!["nested"].GetProperty(propertyName: "player").GetInt32()
        );
        Assert.False(condition: metadata.Custom["nested"].TryGetProperty(
            propertyName: "removed",
            value: out _
        ));
        Assert.Equal(
            3,
            metadata.Custom["nested"].GetProperty(propertyName: "added").GetInt32()
        );
        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                b,
                a,
                upgraded,
                out var reversed,
                out reason
            ),
            userMessage: reason
        );
        AssertOnlyDefinitionsChanged(
            after: reversed!,
            before: original
        );
        // Custom JsonElement objects preserve insertion order. A removed/reintroduced key can move while its
        // value and every unrelated field remain identical; object key order is not author metadata content.
        foreach (var pair in new[] { (original.Server.DefinitionJson, reversed!.Server.DefinitionJson), (original.Server.BaseDefinitionJson, reversed.Server.BaseDefinitionJson) }) {
            Assert.True(condition: System.Text.Json.Nodes.JsonNode.DeepEquals(
                node1: System.Text.Json.Nodes.JsonNode.Parse(pair.Item1),
                node2: System.Text.Json.Nodes.JsonNode.Parse(pair.Item2)
            ));
        }
        var edited = a with { Metadata = a.Metadata! with { Tags = ["old", "player"] } };
        var conflict = original with { Server = original.Server with { DefinitionJson = WorldDefinitionSerialization.Serialize(definition: edited) } };

        Assert.False(condition: WorldReleaseMetadataTransition.TryApply(
            a,
            b,
            conflict,
            out _,
            out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "metadata/tags"
        );
    }
    [Fact]
    public void LiteralCustomNullAndMissingMembersRemainDistinctAcrossReversal() {
        var a = Fixtures.BuildDocument() with { Metadata = new(Custom: Bag(json: "{\"value\":null}")) };
        var b = a with { Metadata = new(Custom: Bag(json: "{}")) };
        using var fixture = Fixtures.FreshServer(a);
        var original = Capture(fixture: fixture);

        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                a,
                b,
                original,
                out var upgraded,
                out var reason
            ),
            userMessage: reason
        );
        Assert.False(condition: WorldDefinitionSerialization.Deserialize(utf8Json: upgraded!.Server.DefinitionJson).Metadata!.Custom!.ContainsKey(key: "value"));
        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                b,
                a,
                upgraded,
                out var reversed,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            JsonValueKind.Null,
            WorldDefinitionSerialization.Deserialize(utf8Json: reversed!.Server.DefinitionJson).Metadata!.Custom!["value"].ValueKind
        );
        Assert.Equal(
            WorldAuthorityCheckpointCodec.Encode(checkpoint: original),
            WorldAuthorityCheckpointCodec.Encode(checkpoint: reversed)
        );
    }
    [Fact]
    public void MetadataUpgradeAndRollbackPreserveLatestGameplayAndUndoHistory() {
        var a = Fixtures.BuildDocument() with {
            Metadata = new(
            Title: "Release A",
            Description: "authored"
        ),
        };
        var b = a with { Metadata = a.Metadata! with { Title = "Release B" } };
        // A live metadata edit outside the authored delta and a normal journaled edit both survive.
        using var source = Fixtures.FreshServer(a with { Metadata = a.Metadata! with { Description = "operator note" } });

        source.Server.EnqueueMutation(new WorldMutation.SetRenderDefaults(
            Principal: Principal.Console,
            Render: source.Server.Definition.Render with { AmbientOcclusion = !source.Server.Definition.Render.AmbientOcclusion }
        ));
        source.Step();
        source.Step();
        var original = Capture(fixture: source);

        Assert.NotEmpty(collection: original.Server.Journal);
        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                a,
                a,
                original,
                out var unchanged,
                out var unchangedReason
            ),
            userMessage: unchangedReason
        );
        Assert.Equal(
            WorldAuthorityCheckpointCodec.Encode(checkpoint: original),
            WorldAuthorityCheckpointCodec.Encode(checkpoint: unchanged!)
        );
        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                a,
                b,
                original,
                out var upgraded,
                out var reason
            ),
            userMessage: reason
        );
        AssertOnlyDefinitionsChanged(
            after: upgraded!,
            before: original
        );
        AssertMetadata(
            checkpoint: upgraded!,
            description: "operator note",
            title: "Release B"
        );

        using var candidate = Fixtures.FreshServer(WorldDefinitionSerialization.Deserialize(utf8Json: upgraded!.Server.DefinitionJson));

        candidate.Server.RestoreCheckpoint(checkpoint: upgraded);
        candidate.Step();
        candidate.Step();
        var latest = Capture(fixture: candidate);

        Assert.True(condition: (latest.Server.LastCompletedTick > original.Server.LastCompletedTick));
        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                b,
                a,
                latest,
                out var rolledBack,
                out reason
            ),
            userMessage: reason
        );
        AssertOnlyDefinitionsChanged(
            after: rolledBack!,
            before: latest
        );
        AssertMetadata(
            checkpoint: rolledBack!,
            description: "operator note",
            title: "Release A"
        );
        using var restored = Fixtures.FreshServer(WorldDefinitionSerialization.Deserialize(utf8Json: rolledBack!.Server.DefinitionJson));

        restored.Server.RestoreCheckpoint(checkpoint: rolledBack);
        restored.Server.EnqueueUndo(
            1,
            Principal.Console
        );
        restored.Step();
        Assert.Equal(
            a.Render.AmbientOcclusion,
            restored.Server.Definition.Render.AmbientOcclusion
        );
        Assert.Equal(
            "Release A",
            restored.Server.Definition.Metadata!.Title
        );
        Assert.Equal(
            "operator note",
            restored.Server.Definition.Metadata.Description
        );
        Assert.Empty(collection: Capture(fixture: restored).Server.Journal);
        // Preparing either transition did not mutate its source checkpoint.
        AssertMetadata(
            checkpoint: original,
            description: "operator note",
            title: "Release A"
        );
        AssertMetadata(
            checkpoint: latest,
            description: "operator note",
            title: "Release B"
        );
    }
    // Both packages are proved through a drawn copy: a package whose census reads a row no boot can draw passes the
    // undrawn document's own validation and is still refused, and the same package with a boot draw is admitted.
    [Fact]
    public void BothPackageDefinitionsAreProvedThroughADrawnCopy() {
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument());
        var checkpoint = Capture(fixture: fixture);
        var drawn = PublishableDefinitionLawTests.CensusDefinition(draw: new Draw(
            Generator: new StateGenerator(
                Source: GeneratorSource.WeightedNumeric,
                Weighted: [new GeneratorWeightedNumeric(Value: 8L, Weight: 1UL)]
            ),
            Timing: DrawTiming.Boot
        ));
        var undrawable = PublishableDefinitionLawTests.CensusDefinition(draw: null);

        Assert.True(
            condition: WorldReleaseMetadataTransition.TryApply(
                drawn,
                (drawn with { Metadata = new(Title: "published") }),
                checkpoint,
                out var transitioned,
                out var reason
            ),
            userMessage: reason
        );
        Assert.NotNull(@object: transitioned);
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: undrawable,
                reason: out reason
            ),
            userMessage: reason
        );

        foreach (var (before, after) in new[] { (undrawable, drawn), (drawn, undrawable) }) {
            Assert.False(condition: WorldReleaseMetadataTransition.TryApply(
                before,
                after,
                checkpoint,
                out transitioned,
                out reason
            ));
            Assert.Null(@object: transitioned);
            Assert.Contains(
                actualString: reason,
                expectedSubstring: "bodies.capacityRow 'census'"
            );
        }
    }
    [Fact]
    public void RuntimeChangesAndInvalidMetadataAreNotAdmittedByTheMetadataRule() {
        var a = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(a);
        var checkpoint = Capture(fixture: fixture);

        Assert.False(condition: WorldReleaseMetadataTransition.TryApply(
            a,
            a with { RenderRaw = a.Render with { AmbientOcclusion = !a.Render.AmbientOcclusion } },
            checkpoint,
            out var refused,
            out var reason
        ));
        Assert.Null(@object: refused);
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "outside metadata"
        );
        Assert.False(condition: WorldReleaseMetadataTransition.TryApply(
            a,
            a with { Metadata = new(Title: "forged]") },
            checkpoint,
            out refused,
            out _
        ));
        Assert.Null(@object: refused);
        var pending = checkpoint with {
            Server = checkpoint.Server with {
                Pending = [new WorldPendingOpCheckpoint.Undo(
                1,
                Principal.Console,
                0,
                0
            )],
            },
        };

        Assert.False(condition: WorldReleaseMetadataTransition.TryApply(
            a,
            a with { Metadata = new(Title: "valid") },
            pending,
            out refused,
            out reason
        ));
        Assert.Null(@object: refused);
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "pending document work"
        );
    }
}
