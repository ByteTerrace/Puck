using System.Text.Json;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseMetadataTransitionLawTests {
    [Fact]
    public void MetadataUpgradeAndRollbackPreserveLatestGameplayAndUndoHistory() {
        var a = Fixtures.BuildDocument() with { Metadata = new(Title: "Release A", Description: "authored") };
        var b = a with { Metadata = a.Metadata! with { Title = "Release B" } };
        // A live metadata edit outside the authored delta and a normal journaled edit both survive.
        using var source = Fixtures.FreshServer(a with { Metadata = a.Metadata! with { Description = "operator note" } });
        source.Server.EnqueueMutation(new WorldMutation.SetRenderDefaults(WorldPrincipal.Console,
            source.Server.Definition.Render with { AmbientOcclusion = !source.Server.Definition.Render.AmbientOcclusion }));
        source.Step();
        source.Step();
        var original = Capture(source);
        Assert.NotEmpty(original.Server.Journal);
        Assert.True(WorldReleaseMetadataTransition.TryApply(a, a, original, out var unchanged, out var unchangedReason), unchangedReason);
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(original), WorldAuthorityCheckpointCodec.Encode(unchanged!));
        Assert.True(WorldReleaseMetadataTransition.TryApply(a, b, original, out var upgraded, out var reason), reason);
        AssertOnlyDefinitionsChanged(original, upgraded!);
        AssertMetadata(upgraded!, "Release B", "operator note");

        using var candidate = Fixtures.FreshServer(WorldDefinitionSerialization.Deserialize(upgraded!.Server.DefinitionJson));
        candidate.Server.RestoreCheckpoint(upgraded);
        candidate.Step();
        candidate.Step();
        var latest = Capture(candidate);
        Assert.True(latest.Server.LastCompletedTick > original.Server.LastCompletedTick);
        Assert.True(WorldReleaseMetadataTransition.TryApply(b, a, latest, out var rolledBack, out reason), reason);
        AssertOnlyDefinitionsChanged(latest, rolledBack!);
        AssertMetadata(rolledBack!, "Release A", "operator note");
        using var restored = Fixtures.FreshServer(WorldDefinitionSerialization.Deserialize(rolledBack!.Server.DefinitionJson));
        restored.Server.RestoreCheckpoint(rolledBack);
        restored.Server.EnqueueUndo(1, WorldPrincipal.Console);
        restored.Step();
        Assert.Equal(a.Render.AmbientOcclusion, restored.Server.Definition.Render.AmbientOcclusion);
        Assert.Equal("Release A", restored.Server.Definition.Metadata!.Title);
        Assert.Equal("operator note", restored.Server.Definition.Metadata.Description);
        Assert.Empty(Capture(restored).Server.Journal);
        // Preparing either transition did not mutate its source checkpoint.
        AssertMetadata(original, "Release A", "operator note");
        AssertMetadata(latest, "Release B", "operator note");
    }

    [Theory]
    [InlineData(false, "player edit")]
    [InlineData(true, "player edit")]
    [InlineData(false, "B")]
    public void ConflictsInEitherLiveDefinitionOrUndoBaseRefuseWithoutChangingAnyBytes(bool undoBase, string title) {
        var a = Fixtures.BuildDocument() with { Metadata = new(Title: "A") };
        var b = a with { Metadata = new(Title: "B") };
        using var fixture = Fixtures.FreshServer(a);
        var current = Capture(fixture);
        var conflict = WorldDefinitionSerialization.Serialize(a with { Metadata = new(Title: title) });
        current = current with { Server = undoBase
            ? current.Server with { BaseDefinitionJson = conflict }
            : current.Server with { DefinitionJson = conflict } };
        var bytes = WorldAuthorityCheckpointCodec.Encode(current);
        Assert.False(WorldReleaseMetadataTransition.TryApply(a, b, current, out var result, out var reason));
        Assert.Null(result);
        Assert.Contains(undoBase ? "undo base/metadata/title" : "live/metadata/title", reason);
        Assert.Equal(bytes, WorldAuthorityCheckpointCodec.Encode(current));
    }

    [Fact]
    public void CustomMembersMergeIndependentlyAndArraysRemainIndivisible() {
        var a = Fixtures.BuildDocument() with { Metadata = new(Tags: ["old"], Custom: Bag("{\"nested\":{\"removed\":1,\"changed\":2}}")) };
        var b = a with { Metadata = new(Tags: ["new"], Custom: Bag("{\"nested\":{\"added\":3,\"changed\":4}}")) };
        using var fixture = Fixtures.FreshServer(a with { Metadata = a.Metadata! with {
            Custom = Bag("{\"nested\":{\"removed\":1,\"changed\":2,\"player\":5},\"unrelated\":true}") } });
        var original = Capture(fixture);
        Assert.True(WorldReleaseMetadataTransition.TryApply(a, b, original, out var upgraded, out var reason), reason);
        var metadata = WorldDefinitionSerialization.Deserialize(upgraded!.Server.DefinitionJson).Metadata!;
        Assert.Equal(5, metadata.Custom!["nested"].GetProperty("player").GetInt32());
        Assert.False(metadata.Custom["nested"].TryGetProperty("removed", out _));
        Assert.Equal(3, metadata.Custom["nested"].GetProperty("added").GetInt32());
        Assert.True(WorldReleaseMetadataTransition.TryApply(b, a, upgraded, out var reversed, out reason), reason);
        AssertOnlyDefinitionsChanged(original, reversed!);
        // Custom JsonElement objects preserve insertion order. A removed/reintroduced key can move while its
        // value and every unrelated field remain identical; object key order is not author metadata content.
        foreach (var pair in new[] { (original.Server.DefinitionJson, reversed!.Server.DefinitionJson), (original.Server.BaseDefinitionJson, reversed.Server.BaseDefinitionJson) }) {
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(pair.Item1), System.Text.Json.Nodes.JsonNode.Parse(pair.Item2)));
        }
        var edited = a with { Metadata = a.Metadata! with { Tags = ["old", "player"] } };
        var conflict = original with { Server = original.Server with { DefinitionJson = WorldDefinitionSerialization.Serialize(edited) } };
        Assert.False(WorldReleaseMetadataTransition.TryApply(a, b, conflict, out _, out reason));
        Assert.Contains("metadata/tags", reason);
    }

    [Fact]
    public void AddingAndRemovingMetadataPreservesUnrelatedLiveMembers() {
        var a = Fixtures.BuildDocument() with { Metadata = null };
        var b = a with { Metadata = new(Title: "published") };
        using var fixture = Fixtures.FreshServer(a with { Metadata = new(Description: "live note") });
        var original = Capture(fixture);
        Assert.True(WorldReleaseMetadataTransition.TryApply(a, b, original, out var upgraded, out var reason), reason);
        Assert.True(WorldReleaseMetadataTransition.TryApply(b, a, upgraded!, out var reversed, out reason), reason);
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(original), WorldAuthorityCheckpointCodec.Encode(reversed!));
    }

    [Fact]
    public void LiteralCustomNullAndMissingMembersRemainDistinctAcrossReversal() {
        var a = Fixtures.BuildDocument() with { Metadata = new(Custom: Bag("{\"value\":null}")) };
        var b = a with { Metadata = new(Custom: Bag("{}")) };
        using var fixture = Fixtures.FreshServer(a);
        var original = Capture(fixture);
        Assert.True(WorldReleaseMetadataTransition.TryApply(a, b, original, out var upgraded, out var reason), reason);
        Assert.False(WorldDefinitionSerialization.Deserialize(upgraded!.Server.DefinitionJson).Metadata!.Custom!.ContainsKey("value"));
        Assert.True(WorldReleaseMetadataTransition.TryApply(b, a, upgraded, out var reversed, out reason), reason);
        Assert.Equal(JsonValueKind.Null, WorldDefinitionSerialization.Deserialize(reversed!.Server.DefinitionJson).Metadata!.Custom!["value"].ValueKind);
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(original), WorldAuthorityCheckpointCodec.Encode(reversed));
    }

    [Fact]
    public void RuntimeChangesAndInvalidMetadataAreNotAdmittedByTheMetadataRule() {
        var a = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(a);
        var checkpoint = Capture(fixture);
        Assert.False(WorldReleaseMetadataTransition.TryApply(a, a with { RenderRaw = a.Render with { AmbientOcclusion = !a.Render.AmbientOcclusion } }, checkpoint, out var refused, out var reason));
        Assert.Null(refused);
        Assert.Contains("outside metadata", reason);
        Assert.False(WorldReleaseMetadataTransition.TryApply(a, a with { Metadata = new(Title: "forged]") }, checkpoint, out refused, out _));
        Assert.Null(refused);
        var pending = checkpoint with { Server = checkpoint.Server with {
            Pending = [new WorldPendingOpCheckpoint.Undo(1, WorldPrincipal.Console, 0, 0)] } };
        Assert.False(WorldReleaseMetadataTransition.TryApply(a, a with { Metadata = new(Title: "valid") }, pending, out refused, out reason));
        Assert.Null(refused);
        Assert.Contains("pending document work", reason);
    }

    private static Dictionary<string, JsonElement> Bag(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private static WorldAuthorityCheckpoint Capture(WorldFixture fixture) {
        Assert.True(fixture.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), reason);
        return checkpoint!;
    }
    private static void AssertMetadata(WorldAuthorityCheckpoint checkpoint, string title, string description) {
        foreach (var bytes in new[] { checkpoint.Server.DefinitionJson, checkpoint.Server.BaseDefinitionJson }) {
            var metadata = WorldDefinitionSerialization.Deserialize(bytes).Metadata!;
            Assert.Equal(title, metadata.Title);
            Assert.Equal(description, metadata.Description);
        }
    }
    private static void AssertOnlyDefinitionsChanged(WorldAuthorityCheckpoint before, WorldAuthorityCheckpoint after) {
        foreach (var pair in new[] { (before.Server.DefinitionJson, after.Server.DefinitionJson), (before.Server.BaseDefinitionJson, after.Server.BaseDefinitionJson) }) {
            var original = WorldDefinitionSerialization.Deserialize(pair.Item1) with { Metadata = null };
            var changed = WorldDefinitionSerialization.Deserialize(pair.Item2) with { Metadata = null };
            Assert.Equal(WorldDefinitionSerialization.Serialize(original), WorldDefinitionSerialization.Serialize(changed));
        }
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(before), WorldAuthorityCheckpointCodec.Encode(after with { Server = after.Server with {
            DefinitionJson = before.Server.DefinitionJson, BaseDefinitionJson = before.Server.BaseDefinitionJson } }));
    }
}
