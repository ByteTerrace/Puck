using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the sector TEXT carry-through — the one radial fact that crosses every layer of the stack and was
/// asserted nowhere: an authored sector <c>Text</c> has to survive <see cref="BindingProfile.Compile"/> into the
/// sector's <see cref="BindingActivation"/>, ride <see cref="PagedInputBindings.WheelFor"/> out to the presenter
/// unchanged, and reach the seat's deterministic lane through
/// <see cref="BindingWheelCommitResult.Dispatch"/> as the submitted line <c>&lt;command&gt; &lt;text&gt;</c>.
/// Asserted from the LANE rather than off the activation, because the payload is deliberately opaque to
/// presentation (<see cref="BindingWheelSectorView.Id"/> carries the presentation-side identity instead) — so the
/// submitted line is the only place the carry-through is observable, and every hop between is a plain field copy
/// whose loss is invisible until an authored radial silently activates a bare verb.</summary>
public sealed class BindingWheelSectorTextTests {
    private const string ActionCommand = "test.action";

    [Fact]
    public void TwoSectorsOnOneCommandAreDistinguishedOnlyByWhatTheySubmit() {
        var wheel = Wheel();

        // The setup's whole point: the payload cannot be derived from the command, because both sectors name the
        // same one. Only the authored text separates them, which is exactly what the two tests below measure.
        Assert.Equal(
            actual: wheel.Rings[0].Sectors[0].Command,
            expected: ActionCommand
        );
        Assert.Equal(
            actual: wheel.Rings[0].Sectors[1].Command,
            expected: ActionCommand
        );
        Assert.Equal(
            actual: wheel.Rings[0].Sectors[0].Id,
            expected: "north"
        );
        Assert.Equal(
            actual: wheel.Rings[0].Sectors[1].Id,
            expected: "south"
        );
    }
    [Fact]
    public void ACommittedSectorSubmitsItsCommandAndTextAsOneLine() {
        var router = Router();
        var wheel = Wheel();
        var outcome = BindingWheelCommitResult.Dispatch(
            activation: wheel.Rings[0].Sectors[0].Activation,
            label: "North",
            ring: 0,
            router: router,
            sector: 0,
            slot: 0
        );

        Assert.Equal(
            actual: outcome.Status,
            expected: BindingWheelCommitStatus.Dispatched
        );

        var entry = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(
            tick: 1UL,
            windowEndTick: ulong.MaxValue
        ).Lanes).Entries);

        // The router composes the line itself — the activation carries the payload alone, so a presenter can never
        // author a line naming a different command than the one the registry resolved.
        Assert.Equal(
            actual: entry.Text,
            expected: $"{ActionCommand} north"
        );
    }
    [Fact]
    public void AValueOnlySectorSubmitsNoLineAtAll() {
        var router = Router();
        var wheel = Wheel();
        var outcome = BindingWheelCommitResult.Dispatch(
            activation: wheel.Rings[0].Sectors[1].Activation,
            label: "South",
            ring: 0,
            router: router,
            sector: 1,
            slot: 0
        );

        Assert.Equal(
            actual: outcome.Status,
            expected: BindingWheelCommitStatus.Dispatched
        );

        var entry = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(
            tick: 1UL,
            windowEndTick: ulong.MaxValue
        ).Lanes).Entries);

        // An unauthored payload stays null rather than becoming a trailing-space line: the text path is the decoded
        // console grammar, and "test.action " parses as a different submission than no submission. This is also what
        // proves the sibling sector's payload is not shared through the compiled ring.
        Assert.Null(@object: entry.Text);
    }

    private static InputRouter Router() => new(
        registry: new CommandRegistry(modules: [new ProbeModule()]),
        bindings: new EmptyBindings(),
        principalResolver: new ConsolePrincipal()
    );
    // One ring, two sectors on the SAME command: the first authors a text payload and the second deliberately
    // authors none.
    private static BindingWheelView Wheel() {
        var profile = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(
                    Id: "hold",
                    Entries: []
                )
            )],
            Wheels: [new BindingWheelDefinition(
                Id: "menu",
                Group: "play",
                HoldPages: ["hold"],
                Rings: [new BindingPageDefinition(
                    Id: "actions",
                    Entries: [
                        new BindingPageEntryDefinition(
                            Sources: null,
                            Command: ActionCommand,
                            Id: "north",
                            Text: "north"
                        ),
                        new BindingPageEntryDefinition(
                            Sources: null,
                            Command: ActionCommand,
                            Id: "south"
                        ),
                    ]
                )]
            )]
        ));

        return new PagedInputBindings(profile: profile).WheelFor(slot: 0)!;
    }

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: ActionCommand,
                description: "Radial sector probe.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
}
