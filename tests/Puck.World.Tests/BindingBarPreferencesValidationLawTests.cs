using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the player-profile-side binding-bar LOOK preferences to the same strictness as the world-side
/// layout: an out-of-range scale refuses by name at validation rather than being silently dropped at the runtime
/// resolver, while a finite positive scale (and an absent preferences block) passes.</summary>
public sealed class BindingBarPreferencesValidationLawTests {
    private static WorldDefinition WithPreferences(BindingBarPreferences? preferences) => Fixtures.BuildDocument() with {
        BindingOverlaysRaw = [
            new WorldBindingOverlay(
            Id: "prefs-law",
            Document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [],
                Chords: [new BindingChordDefinition(
                        Group: "prefsLaw",
                        Page: new BindingPageDefinition(
                            Id: "base",
                            Entries: []
                        )
                    )],
                BindingBar: preferences
            )
        ),
        ],
    };

    [Fact]
    public void AbsentPreferencesPasses() =>
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: WithPreferences(preferences: null),
                neighbours: null,
                reason: out var reason
            ),
            userMessage: reason
        );
    // Each lane's admitted range: contrastBoost [1, 2], scale finite and positive, uiScale [0.5, 2].
    public static TheoryData<string, float, bool> Cases() => new() {
        { "contrastBoost", 1f, true },
        { "contrastBoost", 2f, true },
        { "contrastBoost", 1.5f, true },
        { "contrastBoost", 0.99f, false },
        { "contrastBoost", 2.01f, false },
        { "contrastBoost", float.NaN, false },
        { "scale", 1.5f, true },
        { "scale", 0.01f, true },
        { "scale", 0f, false },
        { "scale", -1f, false },
        { "scale", float.NaN, false },
        { "scale", float.PositiveInfinity, false },
        { "uiScale", 0.5f, true },
        { "uiScale", 2f, true },
        { "uiScale", 1f, true },
        { "uiScale", 0.49f, false },
        { "uiScale", 2.01f, false },
        { "uiScale", float.PositiveInfinity, false },
    };
    [MemberData(memberName: nameof(Cases))]
    [Theory]
    public void AProfileLookLaneOutsideItsRangeRefusesByName(string lane, float value, bool valid) {
        var definition = WithPreferences(preferences: lane switch {
            "contrastBoost" => new BindingBarPreferences(ContrastBoost: value),
            "scale" => new BindingBarPreferences(Scale: value),
            _ => new BindingBarPreferences(UiScale: value),
        });

        if (valid) {
            Laws.Validates(definition: definition);
            return;
        }
        Laws.Refuses(
            definition: definition,
            needle: $"bindingOverlays[0].document.bindingBar.{lane}"
        );
    }
}
