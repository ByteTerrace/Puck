using Xunit;

using Puck.Commands;

namespace Puck.World.Tests;

/// <summary>Proves a channel scale is always a finite member of its authored [-1, 1] domain.</summary>
public sealed class BindingChannelScaleLawTests {
    [Fact]
    public void NonFiniteChannelScalesAreStructurallyRefusedOnPagesAndChords() {
        foreach (var scale in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity }) {
            _ = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: Document(pageScale: scale, chordScale: null)));
            _ = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: Document(pageScale: null, chordScale: scale)));
        }
    }

    private static BindingProfileDocument Document(float? pageScale, float? chordScale) => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [new BindingModifierDefinition(Id: "shift", Source: "key.shift")],
        Chords: [
            new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(
                    Id: "base",
                    Entries: [new BindingPageEntryDefinition(Source: "key.fire", Channel: new ChannelRef.Name(Value: "fire"), Scale: pageScale)]
                )
            ),
            new BindingChordDefinition(
                Group: "play",
                Chord: ["shift"],
                Command: new BindingCommandDefinition(Channel: new ChannelRef.Name(Value: "fire"), Scale: chordScale)
            ),
        ]
    );
}
