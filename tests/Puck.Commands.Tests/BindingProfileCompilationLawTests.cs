using System.Text.Json;
using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Laws the binding compiler holds over a whole document: the channel release contract, malformed
/// identifiers, page inheritance, the page view a bar reads, and the empty profile.</summary>
public sealed class BindingProfileCompilationLawTests {
    private const string TextCommand = "test.line";

    private static CompiledBindingProfile Compile(
        IReadOnlyList<BindingChordDefinition> rows,
        IReadOnlyList<BindingModifierDefinition>? modifiers = null,
        IReadOnlyList<BindingContextDefinition>? contexts = null,
        IReadOnlyList<BindingWheelDefinition>? wheels = null
    ) {
        return BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: (modifiers ?? []),
            Chords: rows,
            Contexts: contexts,
            Wheels: wheels
        ));
    }
    // Distinct four-member ordered chords over `alphabet` raw sources, in odometer order.
    private static List<string[]> DistinctChords(int alphabet, int count) {
        var result = new List<string[]>(capacity: count);

        for (var first = 0; (first < alphabet); first++) {
            for (var second = 0; (second < alphabet); second++) {
                for (var third = 0; (third < alphabet); third++) {
                    for (var fourth = 0; (fourth < alphabet); fourth++) {
                        if (
                            (first == second) || (first == third) || (first == fourth) ||
                            (second == third) || (second == fourth) || (third == fourth)
                        ) {
                            continue;
                        }

                        result.Add(item: [$"key.m{first}", $"key.m{second}", $"key.m{third}", $"key.m{fourth}"]);

                        if (result.Count == count) {
                            return result;
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException(message: $"An alphabet of {alphabet} yields fewer than {count} distinct chords.");
    }
    private static BindingChordDefinition Resting(params BindingPageEntryDefinition[] entries) {
        return new BindingChordDefinition(
            Group: "g",
            Chord: [],
            Page: new BindingPageDefinition(
                Id: "base",
                Entries: entries
            )
        );
    }

    [Fact]
    public void AHoldChannelChordRowDispatchesItsReleaseWithoutAuthoringHoldRelease() {
        // Only the channel verb's handler frees a channel, and CommandRegistry.ApplySnapshot drops any edge whose
        // Dispatch is false — so a channel destination whose break edge does not dispatch latches on forever.
        var bindings = new PagedInputBindings(profile: Compile(rows: [
            Resting(),
            new BindingChordDefinition(
                Group: "g",
                Held: ["key.a"],
                Command: new BindingCommandDefinition(Channel: new ChannelRef.Name(Value: "thrust"))
            ),
        ]));

        _ = bindings.Resolve(
            pressesWithheld: false,
            signal: InputSignal.Press(source: "key.a"),
            slot: 0
        );

        var pressed = bindings.DrainChordEdges(slot: 0);

        Assert.Equal(expected: 1, actual: pressed.Length);
        Assert.Equal(expected: CommandPhase.Started, actual: pressed[0].Phase);
        Assert.True(condition: pressed[0].Dispatch);

        _ = bindings.Resolve(
            pressesWithheld: false,
            signal: InputSignal.Release(source: "key.a"),
            slot: 0
        );

        var released = bindings.DrainChordEdges(slot: 0);

        Assert.Equal(expected: 1, actual: released.Length);
        Assert.Equal(expected: CommandPhase.Completed, actual: released[0].Phase);
        Assert.True(condition: released[0].Dispatch);
    }
    [Fact]
    public void ACommandChordRowStillWithholdsItsReleaseAtTheDefault() {
        // The other half of the same law: a plain command destination is momentary and has nothing to free, so the
        // widened channel rule must not have widened this one too.
        var bindings = new PagedInputBindings(profile: Compile(rows: [
            Resting(),
            new BindingChordDefinition(
                Group: "g",
                Held: ["key.a"],
                Command: new BindingCommandDefinition(Command: "test.action")
            ),
        ]));

        _ = bindings.Resolve(
            pressesWithheld: false,
            signal: InputSignal.Press(source: "key.a"),
            slot: 0
        );
        _ = bindings.DrainChordEdges(slot: 0);
        _ = bindings.Resolve(
            pressesWithheld: false,
            signal: InputSignal.Release(source: "key.a"),
            slot: 0
        );

        var released = bindings.DrainChordEdges(slot: 0);

        Assert.Equal(expected: CommandPhase.Completed, actual: released[0].Phase);
        Assert.False(condition: released[0].Dispatch);
    }
    [Fact]
    public void ANullGroupIsRefusedByRowRatherThanCrashingTheCompiler() {
        // A JSON `null` never reaches DocumentIdentifier's converter, so the property arrives null and the implicit
        // string conversion would throw a NullReferenceException past every caller (all of which catch
        // ArgumentException only). Each of the three group-bearing row kinds must refuse it by name instead.
        var chordRow = Assert.Throws<ArgumentException>(testCode: static () => Compile(rows: [
            new BindingChordDefinition(
                Group: null!,
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: [])
            ),
        ]));

        Assert.Contains(actualString: chordRow.Message, expectedSubstring: "Chord row 0");

        var contextRow = Assert.Throws<ArgumentException>(testCode: static () => Compile(
            contexts: [new BindingContextDefinition(Family: "roster", Group: null!, State: "pending")],
            rows: [Resting()]
        ));

        Assert.Contains(actualString: contextRow.Message, expectedSubstring: "Contexts row 0");

        var wheelRow = Assert.Throws<ArgumentException>(testCode: static () => Compile(
            rows: [
                Resting(),
                new BindingChordDefinition(
                    Group: "g",
                    Held: ["key.a"],
                    Page: new BindingPageDefinition(Id: "hold", Entries: [])
                ),
            ],
            wheels: [new BindingWheelDefinition(
                Id: "w",
                Group: null!,
                HoldPages: ["hold"],
                Rings: [new BindingPageDefinition(
                    Id: "ring",
                    Entries: [
                        new BindingPageEntryDefinition(Sources: null, Command: "test.one"),
                        new BindingPageEntryDefinition(Sources: null, Command: "test.two"),
                    ]
                )]
            )]
        ));

        Assert.Contains(actualString: wheelRow.Message, expectedSubstring: "Wheel \"w\"");
    }
    [Fact]
    public void AWheelSectorsAuthoredTextIsSubmittedAsTheCommandsLine() {
        // The other half of the sector-text contract: BindingProfileValidationTests pins what the compiler REFUSES,
        // this pins that an accepted payload actually reaches the wire. A sector commits through the seat's own
        // input-router lane exactly as a bound press does, so the gate the text bound and the wire-args check now
        // guard is only worth having if the line arrives — a payload dropped in compilation would validate cleanly
        // and then submit the bare verb. The activation is deliberately opaque, so this reads the SNAPSHOT.
        //
        // The resting page doubles as the wheel's hold page, so a fresh slot presents the radial with no input.
        var profile = Compile(
            rows: [Resting()],
            wheels: [new BindingWheelDefinition(
                Id: "w",
                Group: "g",
                HoldPages: ["base"],
                Rings: [new BindingPageDefinition(
                    Id: "ring",
                    Entries: [
                        new BindingPageEntryDefinition(Sources: null, Command: TextCommand, Text: "north"),
                        new BindingPageEntryDefinition(Sources: null, Command: TextCommand),
                    ]
                )]
            )]
        );
        var bindings = new PagedInputBindings(profile: profile);
        var sectors = bindings.WheelFor(slot: 0)!.Rings[0].Sectors;
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new TextProbeModule()]),
            bindings: bindings,
            principalResolver: new SeatPrincipals()
        );

        Assert.True(condition: router.Activate(slot: 0, activation: sectors[0].Activation));
        Assert.True(condition: router.Activate(slot: 0, activation: sectors[1].Activation));

        var entries = Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries.ToArray();

        Assert.Equal(actual: entries.Length, expected: 2);
        Assert.Equal(actual: entries[0].Text, expected: $"{TextCommand} north");
        Assert.All(collection: entries, action: static entry => Assert.Equal(actual: entry.Origin, expected: CommandOrigin.Binding));
        // The text-free sector rides the same door and submits no line at all, so the payload is the sector's own
        // authored field rather than anything the activation path synthesizes.
        Assert.Null(@object: entries[1].Text);
    }
    [Fact]
    public void AnUnresolvedStateReferenceGroupIsRefusedByRowRatherThanCrashingTheCompiler() {
        // The other malformed identifier shape: a "state.<row>" reference the containing world never bound. Reading
        // it throws InvalidOperationException, which is equally invisible to a caller catching ArgumentException.
        var document = JsonSerializer.Deserialize<BindingProfileDocument>(
            json: $$"""
            {
                "version": "{{BindingProfileDocument.CurrentVersion}}",
                "modifiers": [],
                "chords": [{ "group": "state.mode", "chord": [], "page": { "id": "base", "entries": [] } }]
            }
            """,
            options: new JsonSerializerOptions { PropertyNameCaseInsensitive = true, }
        )!;
        var refused = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: document));

        Assert.Contains(actualString: refused.Message, expectedSubstring: "Chord row 0");
    }
    [Fact]
    public void AHeldPageMarksItsHeldMembersRequiredInTheView() {
        // A page selected by an UNORDERED hold rendered no held-modifier chip: the view's required set was built
        // from the chord alone, so the bar showed a page nobody could see the way into.
        var bindings = new PagedInputBindings(profile: Compile(
            modifiers: [
                new BindingModifierDefinition(Id: "shift", Sources: ["key.shift"]),
                new BindingModifierDefinition(Id: "alt", Sources: ["key.alt"]),
            ],
            rows: [
                Resting(),
                new BindingChordDefinition(
                    Group: "g",
                    Held: ["shift"],
                    Page: new BindingPageDefinition(Id: "held", Entries: [])
                ),
            ]
        ));

        Assert.All(
            action: static modifier => Assert.False(condition: modifier.Required),
            collection: bindings.ViewFor(slot: 0).Modifiers
        );

        _ = bindings.Resolve(
            pressesWithheld: false,
            signal: InputSignal.Press(source: "key.shift"),
            slot: 0
        );

        var view = bindings.ViewFor(slot: 0);

        Assert.Equal(expected: "held", actual: view.PageId);
        Assert.True(condition: Assert.Single(
            collection: view.Modifiers,
            predicate: static modifier => (modifier.Id == "shift")
        ).Required);
        Assert.False(condition: Assert.Single(
            collection: view.Modifiers,
            predicate: static modifier => (modifier.Id == "alt")
        ).Required);
    }
    [Fact]
    public void EverySourceOfAMultiSourceEntryFindsItsButton() {
        // The trigger LABEL of a multi-source entry is the comma-joined list, so a consumer matching a physical
        // control against it found nothing at all. ButtonsBySource keys the entry under each source it lists.
        var view = new PagedInputBindings(profile: Compile(rows: [
            Resting(
                new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouth", "key.space"], Command: "test.jump"),
                new BindingPageEntryDefinition(Sources: ["gamepad.buttonEast"], Command: "test.cancel")
            ),
        ])).ViewFor(slot: 0);

        Assert.Equal(
            actual: view.ButtonsBySource["gamepad.buttonSouth"].Command,
            expected: "test.jump"
        );
        Assert.Equal(
            actual: view.ButtonsBySource["key.space"].Command,
            expected: "test.jump"
        );
        // Case is authored-document noise, exactly as it is for the runtime resolve table.
        Assert.Equal(
            actual: view.ButtonsBySource["GAMEPAD.ButtonEast"].Command,
            expected: "test.cancel"
        );
        Assert.False(condition: view.ButtonsBySource.ContainsKey(key: "key.unbound"));
    }
    [Fact]
    public void AnActivatorEntryKeysNoSourceButStillAppearsInButtons() {
        var view = new PagedInputBindings(profile: Compile(rows: [
            Resting(new BindingPageEntryDefinition(
                Sources: null,
                Command: "test.action",
                Activator: new BindingActivatorDefinition(Sequence: ["key.a", "key.b"])
            )),
        ])).ViewFor(slot: 0);

        Assert.Equal(expected: "activator[key.a,key.b]", actual: Assert.Single(collection: view.Buttons).Source);
        Assert.Empty(collection: view.ButtonsBySource);
    }
    [Fact]
    public void ADeepPageInheritanceChainCompilesWithoutExhaustingTheStack() {
        // Inheritance depth is bounded only by the authored page count. A recursive walk died on the stack — an
        // uncatchable process kill — long before the cycle refusal or any other diagnostic could speak.
        const int depth = 20000;

        // Distinct chords come from a SMALL modifier alphabet on purpose: each page view is built over every
        // modifier the profile declares, so one implicit modifier per row would make the document quadratic and
        // measure allocation rather than stack depth.
        var chords = DistinctChords(
            alphabet: 14,
            count: (depth - 1)
        );
        var rows = new List<BindingChordDefinition>(capacity: depth) {
            new(
                Group: "g",
                Chord: [],
                Page: new BindingPageDefinition(
                    Id: "page0",
                    Entries: [new BindingPageEntryDefinition(Sources: ["key.base"], Command: "test.action")]
                )
            ),
        };

        for (var index = 1; (index < depth); index++) {
            rows.Add(item: new BindingChordDefinition(
                Group: "g",
                Chord: chords[(index - 1)],
                Page: new BindingPageDefinition(
                    Id: $"page{index}",
                    Entries: [],
                    Inherits: $"page{(index - 1)}"
                )
            ));
        }

        var profile = Compile(rows: rows);

        Assert.Equal(expected: depth, actual: profile.RowCount);
        Assert.Equal(expected: "page0", actual: profile.RestingPageIdOf(group: "g"));
    }
    [Fact]
    public void PageInheritanceRefusesCyclesAndCrossGroupParents() {
        var cycle = Assert.Throws<ArgumentException>(testCode: static () => Compile(rows: [
            Resting(),
            new BindingChordDefinition(
                Group: "g",
                Held: ["key.a"],
                Page: new BindingPageDefinition(Id: "left", Entries: [], Inherits: "right")
            ),
            new BindingChordDefinition(
                Group: "g",
                Held: ["key.b"],
                Page: new BindingPageDefinition(Id: "right", Entries: [], Inherits: "left")
            ),
        ]));

        Assert.Contains(actualString: cycle.Message, expectedSubstring: "cycle");

        var crossGroup = Assert.Throws<ArgumentException>(testCode: static () => Compile(rows: [
            Resting(),
            new BindingChordDefinition(
                Group: "other",
                Chord: [],
                Page: new BindingPageDefinition(Id: "other-base", Entries: [])
            ),
            new BindingChordDefinition(
                Group: "g",
                Held: ["key.a"],
                Page: new BindingPageDefinition(Id: "borrowed", Entries: [], Inherits: "other-base")
            ),
        ]));

        Assert.Contains(actualString: crossGroup.Message, expectedSubstring: "same group");
    }
    [Fact]
    public void PageInheritanceOverlaysSourceBySourceAndByActivatorIdentity() {
        // The inheriting page replaces an inherited entry SOURCE by source, so a parent row naming three controls
        // survives on the two the child does not claim; an activator entry is claimed by its (mode, sequence).
        var bindings = new PagedInputBindings(profile: Compile(rows: [
            Resting(
                new BindingPageEntryDefinition(Sources: ["key.a", "key.b", "key.c"], Command: "parent.trio"),
                new BindingPageEntryDefinition(Sources: ["key.d"], Command: "parent.solo"),
                new BindingPageEntryDefinition(
                    Sources: null,
                    Command: "parent.activator",
                    Activator: new BindingActivatorDefinition(Sequence: ["key.x", "key.y"])
                )
            ),
            new BindingChordDefinition(
                Group: "g",
                Held: ["key.shift"],
                Page: new BindingPageDefinition(
                    Id: "child",
                    Entries: [
                        new BindingPageEntryDefinition(Sources: ["key.b"], Command: "child.one"),
                        new BindingPageEntryDefinition(
                            Sources: null,
                            Command: "child.activator",
                            Activator: new BindingActivatorDefinition(Sequence: ["key.x", "key.y"])
                        ),
                    ],
                    Inherits: "base"
                )
            ),
        ]));

        Assert.Equal(expected: "base", actual: bindings.ViewFor(slot: 0).PageId);

        _ = bindings.Resolve(
            pressesWithheld: false,
            signal: InputSignal.Press(source: "key.shift"),
            slot: 0
        );

        var child = bindings.ViewFor(slot: 0);

        Assert.Equal(expected: "child", actual: child.PageId);
        // key.a and key.c survive on the narrowed parent row; key.b is claimed by the child.
        Assert.Equal(actual: child.ButtonsBySource["key.a"].Command, expected: "parent.trio");
        Assert.Equal(actual: child.ButtonsBySource["key.c"].Command, expected: "parent.trio");
        Assert.Equal(actual: child.ButtonsBySource["key.b"].Command, expected: "child.one");
        Assert.Equal(actual: child.ButtonsBySource["key.d"].Command, expected: "parent.solo");
        // The parent's activator is shadowed by the child's identical (mode, sequence), not carried beside it.
        Assert.Equal(
            actual: Assert.Single(
                collection: child.Buttons,
                predicate: static button => (button.Source == "activator[key.x,key.y]")
            ).Command,
            expected: "child.activator"
        );
    }
    [Fact]
    public void ADocumentWithNoChordRowsCompilesToTheAnonymousEmptyGroup() {
        foreach (var chords in new IReadOnlyList<BindingChordDefinition>?[] { null, [] }) {
            var profile = BindingProfile.Compile(document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [],
                Chords: chords!
            ));

            Assert.Equal(expected: BindingProfile.EmptyGroup, actual: Assert.Single(collection: profile.Groups));
            Assert.Equal(expected: BindingProfile.EmptyGroup, actual: profile.RestingPageIdOf(group: BindingProfile.EmptyGroup));
            Assert.Empty(collection: new PagedInputBindings(profile: profile).ViewFor(slot: 0).Buttons);
        }
    }
    [Fact]
    public void AGroupWhoseOnlyRowIsACommandHasNoRestingPageAndIsRefused() {
        var refused = Assert.Throws<ArgumentException>(testCode: static () => Compile(rows: [
            Resting(),
            new BindingChordDefinition(
                Group: "commands-only",
                Held: ["key.a"],
                Command: new BindingCommandDefinition(Command: "test.action")
            ),
        ]));

        Assert.Contains(actualString: refused.Message, expectedSubstring: "no resting");

        // And the memberless command row, which has no completion edge to fire on at all.
        var memberless = Assert.Throws<ArgumentException>(testCode: static () => Compile(rows: [
            Resting(),
            new BindingChordDefinition(
                Group: "commands-only",
                Chord: [],
                Command: new BindingCommandDefinition(Command: "test.action")
            ),
        ]));

        Assert.Contains(actualString: memberless.Message, expectedSubstring: "resting row must be a page");
    }
    [Fact]
    public void ARowMemberResolvesToADeclaredModifierIdRegardlessOfCase() {
        // Case-only mismatch used to miss the id lookup, miss the source lookup, and mint an implicit modifier over
        // a control name that does not exist — a row that could never be held, accepted in silence.
        var profile = Compile(
            modifiers: [new BindingModifierDefinition(Id: "Wheel", Sources: ["gamepad.leftShoulder"])],
            rows: [
                Resting(),
                new BindingChordDefinition(
                    Group: "g",
                    Held: ["wheel"],
                    Page: new BindingPageDefinition(Id: "wheel-page", Entries: [])
                ),
            ]
        );

        Assert.Equal(expected: "Wheel", actual: Assert.Single(collection: profile.Modifiers).Id);
    }
    [Fact]
    public void TwoModifierIdsDifferingOnlyByCaseAreRefused() {
        var refused = Assert.Throws<ArgumentException>(testCode: static () => Compile(
            modifiers: [
                new BindingModifierDefinition(Id: "shift", Sources: ["key.shift"]),
                new BindingModifierDefinition(Id: "SHIFT", Sources: ["gamepad.leftShoulder"]),
            ],
            rows: [Resting()]
        ));

        Assert.Contains(actualString: refused.Message, expectedSubstring: "Modifier 1 re-declares id");
    }

    private sealed class SeatPrincipals : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Seat(slot: slot);
    }
    private sealed class TextProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            // Wire-native, because a text-bearing binding row may only target a command that accepts wire arguments.
            yield return CommandDefinition.WithWireArgs(
                name: TextCommand,
                description: "Wheel sector text probe.",
                handler: static (_, _) => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
}
