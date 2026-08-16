using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Proves an authored channel name can lower to a host-owned, replay-stable runtime command without changing
/// the portable binding document shape.</summary>
public sealed class BindingChannelLoweringLawTests {
    [Fact]
    public void RuntimeLoweringReachesPageBindingsChordEdgesAndViews() {
        var channel = new ChannelRef.Name(Value: "late-destination-channel");
        var document = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "shift", Source: "key.shift")],
            Chords: [
                new BindingChordDefinition(
                    Group: "main",
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: "base",
                        Entries: [new BindingPageEntryDefinition(Source: "key.fire", Channel: channel)]
                    )
                ),
                new BindingChordDefinition(
                    Group: "main",
                    Chord: ["shift"],
                    Command: new BindingCommandDefinition(Channel: channel, HoldRelease: true)
                ),
            ]
        );
        const string Lowered = "channel.ordinal.13";
        var compiled = BindingProfile.Compile(channelCommandName: _ => Lowered, document: document);
        var bindings = new PagedInputBindings(profile: compiled);

        Assert.Equal(expected: Lowered, actual: Assert.Single(collection: bindings.Resolve(slot: 0, source: "key.fire")!).Command);

        var view = bindings.ViewFor(slot: 0);

        Assert.Equal(expected: Lowered, actual: Assert.Single(collection: view.Buttons).Command);
        Assert.Equal(expected: Lowered, actual: Assert.Single(collection: view.CommandChords).Command);
    }
}
