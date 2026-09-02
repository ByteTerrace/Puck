using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the injection-seam parity every composition root depends on: <c>Puck.World.Schema</c> validates
/// against hooks a root must install, and a root that installs one fewer than another is a mutation door that admits
/// what the other refuses (the silo revalidates published documents in-process; the desktop revalidates at boot).
/// Both roots and this suite call the SAME <see cref="WorldSchemaVocabularyHooks.Install"/>, so the law that keeps
/// them together is that the shared installer leaves NO seam unwired.</summary>
public sealed class SchemaVocabularyHookParityLawTests {
    /// <summary>Every seam a validator reads is non-null after the shared installer ran. A seam added to
    /// <c>Puck.World.Schema</c> without a line in the shared installer fails here rather than silently skipping its
    /// check in whichever process forgot it.</summary>
    [Fact]
    public void TheSharedInstallerLeavesNoSeamUnwired() {
        Assert.NotNull(@object: BindingVocabularyHook.VocabularyCheck);
        Assert.NotNull(@object: ContextFamilyVocabularyHook.ReservedFamilyNames);
        Assert.NotNull(@object: GamepadFamilyVocabularyHook.IsKnownFamilyName);
        Assert.NotNull(@object: InputSourceVocabularyHook.IsKnownSourceId);
        Assert.NotNull(@object: Protocol.MutationKindVocabularyHook.Describe);
        Assert.NotNull(@object: Protocol.MutationKindVocabularyHook.TryParse);
        Assert.NotNull(@object: WorldExtensionVocabularyHook.PostRenderExtensionCheck);
        Assert.NotNull(@object: WorldExtensionVocabularyHook.ScreenMachineEngineCheck);
    }
    /// <summary>The reserved context-family list is DERIVED from the client's published registry, not mirrored: a
    /// built-in family added there refuses a colliding authored <c>seatModes</c> name without a second edit in the
    /// validator.</summary>
    [Fact]
    public void TheReservedFamilyListIsTheClientRegistry() =>
        Assert.Equal(expected: WorldContextFamilies.Families, actual: ContextFamilyVocabularyHook.ReservedFamilyNames);
    /// <summary>The derivation has teeth: an authored seat-mode family named after a built-in one refuses by name,
    /// beside a passing control that differs only in the name.</summary>
    [Fact]
    public void AuthoredFamilyCollidingWithABuiltInRefusesByName() {
        var denied = WithSeatModeFamily(name: WorldContextFamilies.Engagement);
        var admitted = WithSeatModeFamily(name: "stance");

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "seatModes[0].name");
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "collides with a built-in context family");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static WorldDefinition WithSeatModeFamily(string name) => Fixtures.BuildDocument() with {
        SeatModesRaw = [
            new WorldSeatModeFamily(
                Name: name,
                DefaultState: "rest",
                States: [new WorldSeatModeState(Name: "rest")]
            ),
        ],
    };
}
