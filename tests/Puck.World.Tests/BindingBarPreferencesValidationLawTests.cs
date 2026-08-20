using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the player-profile-side binding-bar LOOK preferences to the same strictness as the world-side
/// layout: an out-of-range scale refuses by name at validation rather than being silently dropped at the runtime
/// resolver, while a finite positive scale (and an absent preferences block) passes.</summary>
public sealed class BindingBarPreferencesValidationLawTests {
    public static IEnumerable<object[]> ScaleCases() {
        yield return [1.5f, true];
        yield return [0.01f, true];
        yield return [0f, false];
        yield return [-1f, false];
        yield return [float.NaN, false];
        yield return [float.PositiveInfinity, false];
    }
    [MemberData(nameof(ScaleCases))]
    [Theory]
    public void ProfileScaleRefusesOutOfRangeByName(float scale, bool valid) {
        var definition = WithPreferences(new BindingBarPreferences(Scale: scale));
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.Equal(expected: valid, actual: admitted);

        if (!valid) {
            Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "bindingOverlays[0].document.bindingBar.scale");
        }
    }

    public static IEnumerable<object[]> ContrastBoostCases() {
        yield return [1f, true];
        yield return [2f, true];
        yield return [1.5f, true];
        yield return [0.99f, false];
        yield return [2.01f, false];
        yield return [float.NaN, false];
    }
    [MemberData(nameof(ContrastBoostCases))]
    [Theory]
    public void ProfileContrastBoostRefusesOutOfRangeByName(float contrastBoost, bool valid) {
        var definition = WithPreferences(new BindingBarPreferences(ContrastBoost: contrastBoost));
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.Equal(expected: valid, actual: admitted);

        if (!valid) {
            Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "bindingOverlays[0].document.bindingBar.contrastBoost");
        }
    }

    public static IEnumerable<object[]> UiScaleCases() {
        yield return [0.5f, true];
        yield return [2f, true];
        yield return [1f, true];
        yield return [0.49f, false];
        yield return [2.01f, false];
        yield return [float.PositiveInfinity, false];
    }
    [MemberData(nameof(UiScaleCases))]
    [Theory]
    public void ProfileUiScaleRefusesOutOfRangeByName(float uiScale, bool valid) {
        var definition = WithPreferences(new BindingBarPreferences(UiScale: uiScale));
        var admitted = WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason);

        Assert.Equal(expected: valid, actual: admitted);

        if (!valid) {
            Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "bindingOverlays[0].document.bindingBar.uiScale");
        }
    }

    [Fact]
    public void AbsentPreferencesPasses() =>
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: WithPreferences(preferences: null), neighbours: null, reason: out var reason), userMessage: reason);

    private static WorldDefinition WithPreferences(BindingBarPreferences? preferences) => Fixtures.BuildDocument() with {
        BindingOverlaysRaw = [
            new WorldBindingOverlay(
                Id: "prefs-law",
                Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion,
                    Modifiers: [],
                    Chords: [new BindingChordDefinition(Group: "prefsLaw", Page: new BindingPageDefinition(Id: "base", Entries: []))],
                    BindingBar: preferences
                )
            ),
        ],
    };
}
