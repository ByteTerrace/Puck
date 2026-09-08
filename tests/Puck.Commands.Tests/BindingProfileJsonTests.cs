using System.Numerics;
using System.Text.Json;

using Puck.Assets.Documents;

using Xunit;

namespace Puck.Commands.Tests;

public sealed class BindingProfileJsonTests {
    private static BindingProfileDocument Document() => new(
        BindingBar: new BindingBarPreferences(
            ContrastBoost: 1.25f,
            HideUnbound: true,
            Scale: 0.9f,
            UiScale: 1.1f
        ),
        Chords: [
            new BindingChordDefinition(
                Group: new DocumentIdentifier(value: "play"),
                Page: new BindingPageDefinition(
                    Entries: [
                        new BindingPageEntryDefinition(
                            Channel: new ChannelRef.Name(Value: "forward"),
                            Scale: -1f,
                            Sources: ["keyboard.s"]
                        ),
                        new BindingPageEntryDefinition(
                            ActivateOn: CommandPhase.Started,
                            Command: "player.jump",
                            Id: "jump",
                            Mode: BindingEntryMode.Hold,
                            Sources: ["keyboard.space"],
                            Text: "high"
                        ),
                        new BindingPageEntryDefinition(
                            Command: "player.throttle",
                            Id: "throttle",
                            Sources: ["keyboard.f1"],
                            Value: CommandValue.Axis(value: 0.75f)
                        ),
                        new BindingPageEntryDefinition(
                            Activator: new BindingActivatorDefinition(
                                Mode: BindingActivatorMode.Tapped,
                                Sequence: ["keyboard.a", "keyboard.b"],
                                TimeoutTicks: 25200
                            ),
                            Command: "player.easterEgg",
                            Sources: null
                        ),
                    ],
                    Id: "base"
                )
            ),
            new BindingChordDefinition(
                Chord: ["look"],
                Command: new BindingCommandDefinition(
                    Command: "player.aim",
                    HoldRelease: true,
                    Icon: "crosshair",
                    Label: "Aim",
                    Value: CommandValue.Orientation(value: new Quaternion(
                        w: 1f,
                        x: 0f,
                        y: 0f,
                        z: 0f
                    ))
                ),
                Group: new DocumentIdentifier(value: "play"),
                Held: ["rt"]
            ),
            new BindingChordDefinition(
                Chord: ["rt"],
                Command: new BindingCommandDefinition(
                    Channel: new ChannelRef.Name(Value: "sprint"),
                    Mode: BindingEntryMode.Toggle,
                    Scale: 1f
                ),
                Group: new DocumentIdentifier(value: "play")
            ),
        ],
        Contexts: [
            new BindingContextDefinition(
                Family: "roster",
                Group: new DocumentIdentifier(value: "play"),
                State: "engaged"
            ),
        ],
        Modifiers: [
            new BindingModifierDefinition(
                Id: "look",
                Sources: ["gamepad.leftTrigger"]
            ),
            new BindingModifierDefinition(
                Id: "rt",
                Sources: ["gamepad.rightTrigger"]
            ),
        ],
        Version: BindingProfileDocument.CurrentVersion,
        Wheels: [
            new BindingWheelDefinition(
                Group: new DocumentIdentifier(value: "play"),
                HoldPages: ["base"],
                Id: "actions",
                Rings: [
                    new BindingPageDefinition(
                        Entries: [
                            new BindingPageEntryDefinition(
                                Command: "view.override",
                                Id: "overhead",
                                Sources: null
                            ),
                            new BindingPageEntryDefinition(
                                Command: "quit",
                                Id: "quit",
                                Sources: null
                            ),
                        ],
                        Id: "ring0"
                    ),
                    new BindingPageDefinition(
                        Entries: [
                            new BindingPageEntryDefinition(
                                Command: "view.override",
                                Id: "action",
                                Sources: null
                            ),
                            new BindingPageEntryDefinition(
                                Command: "player.wave",
                                Id: "wave",
                                Sources: null
                            ),
                        ],
                        Id: "ring1"
                    ),
                ],
                Style: new BindingWheelStyleDefinition(
                    Excursion: new BindingWheelExcursionDefinition(
                        DeadZone: 0.18f,
                        Thresholds: [0.37f]
                    ),
                    Placement: BindingWheelPlacement.ViewportCenter,
                    PointerSelection: BindingWheelSpatialSelectionMode.HitTarget,
                    RingSelection: BindingWheelRingSelectionMode.Excursion
                )
            ),
        ]
    );

    [Fact]
    public void ACommandValueCarriesItsCanonicalShapeWithNoContextRegistration() {
        // The defect this pins: CommandValue's wire shape used to live only on the World's context, so anyone
        // serializing the type from its own assembly got {"kind":0,"raw":{}} — Vector4's components are public
        // FIELDS, which System.Text.Json does not write, so the authored constant was simply gone. The converter
        // now sits on the declaration, so every serializer reaches the same shape.
        var value = CommandValue.Axis(value: new Vector2(
            x: 0.5f,
            y: -0.25f
        ));
        var json = JsonSerializer.Serialize(
            jsonTypeInfo: BindingProfileJsonContext.Default.CommandValue,
            value: value
        );

        using var parsed = JsonDocument.Parse(json: json);

        // The kind is a declared member NAME, not the ordinal 2 the default shape wrote.
        Assert.Equal(actual: parsed.RootElement.GetProperty(propertyName: "kind").GetString(), expected: "Axis2D");
        // And raw is the four components, not the empty object a Vector4's public FIELDS collapse to.
        Assert.Equal(actual: parsed.RootElement.GetProperty(propertyName: "raw").ValueKind, expected: JsonValueKind.Array);
        Assert.Equal(
            actual: parsed.RootElement.GetProperty(propertyName: "raw").EnumerateArray().Select(selector: static component => component.GetSingle()),
            expected: [0.5f, -0.25f, 0f, 0f]
        );
        // No computed accessor rides along: the converter owns the whole object.
        Assert.Equal(
            actual: string.Join(separator: ", ", values: parsed.RootElement.EnumerateObject().Select(selector: static member => member.Name)),
            expected: "kind, raw"
        );
        Assert.Equal(
            actual: JsonSerializer.Deserialize(
                json: json,
                jsonTypeInfo: BindingProfileJsonContext.Default.CommandValue
            ),
            expected: value
        );
    }
    [Fact]
    public void AnEnumCrossesTheWireByNameAndRefusesANumericToken() {
        // StrictEnumConverter now rides each enum's own declaration rather than a context's converter list, so a
        // consumer's context inherits the strict posture without repeating the registration.
        Assert.Equal(
            actual: JsonSerializer.Serialize(
                jsonTypeInfo: BindingProfileJsonContext.Default.BindingEntryMode,
                value: BindingEntryMode.Toggle
            ),
            expected: "\"Toggle\""
        );
        _ = Assert.Throws<JsonException>(testCode: static () => JsonSerializer.Deserialize(
            json: "1",
            jsonTypeInfo: BindingProfileJsonContext.Default.BindingEntryMode
        ));
    }
    [Fact]
    public void TheContextRoundTripsAWholeDocumentWithoutLoss() {
        var document = Document();
        var written = JsonSerializer.Serialize(
            jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument,
            value: document
        );
        var reread = JsonSerializer.Deserialize(
            json: written,
            jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument
        );

        Assert.Equal(
            actual: JsonSerializer.Serialize(
                jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument,
                value: reread
            ),
            expected: written
        );
        // And the round trip is not vacuous: what came back compiles into the same profile the original did.
        var compiled = BindingProfile.Compile(document: document);
        var recompiled = BindingProfile.Compile(document: reread!);

        Assert.Equal(actual: recompiled.RowCount, expected: compiled.RowCount);
        Assert.Equal(actual: recompiled.ActivatorCount, expected: compiled.ActivatorCount);
        Assert.Equal(actual: recompiled.Groups, expected: compiled.Groups);
        // A record's IReadOnlyList member compares by REFERENCE, so the modifier rows are compared by the values
        // that crossed the wire rather than with Assert.Equal over the records themselves.
        Assert.Equal(
            actual: recompiled.Modifiers.Select(selector: static modifier => (modifier.Id, string.Join(separator: ',', values: modifier.Sources), modifier.PressThreshold, modifier.ReleaseThreshold)),
            expected: compiled.Modifiers.Select(selector: static modifier => (modifier.Id, string.Join(separator: ',', values: modifier.Sources), modifier.PressThreshold, modifier.ReleaseThreshold))
        );
    }
    [Fact]
    public void TheContextRefusesAnUnmappedMember() {
        // The strict posture the World's context carries is the context's own here too: an authoring typo fails
        // by name rather than vanishing.
        var written = JsonSerializer.Serialize(
            jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument,
            value: Document()
        ).Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: "\"modifierz\":",
            oldValue: "\"modifiers\":"
        );

        _ = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: written,
            jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument
        ));
    }
}
