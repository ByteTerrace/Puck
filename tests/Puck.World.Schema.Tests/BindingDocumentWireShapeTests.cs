using System.Text.Json;

using Puck.Assets.Documents;
using Puck.Commands;

using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class BindingDocumentWireShapeTests {
    // Every member a written binding document is ALLOWED to carry on a chord row. A computed accessor is not one
    // of them: the record's constructor has no parameter to read it back into, so any extra name here is a member
    // the writer emits and the strict reader (UnmappedMemberHandling.Disallow) then refuses on the way back in.
    private static readonly string[] AllowedChordMembers = [
        "chord",
        "command",
        "group",
        "held",
        "page",
    ];

    private static BindingProfileDocument Document() => new(
        Chords: [
            new BindingChordDefinition(
                Group: new DocumentIdentifier(value: "play"),
                Page: new BindingPageDefinition(
                    Entries: [
                        new BindingPageEntryDefinition(
                            Command: "player.jump",
                            Sources: ["keyboard.space"]
                        ),
                    ],
                    Id: "base"
                )
            ),
            new BindingChordDefinition(
                Chord: ["lt", "rt"],
                Group: new DocumentIdentifier(value: "play"),
                Held: ["look"],
                Page: new BindingPageDefinition(
                    Entries: [],
                    Id: "deep"
                )
            ),
        ],
        Modifiers: [
            new BindingModifierDefinition(
                Id: "look",
                Sources: ["gamepad.leftTrigger"]
            ),
            new BindingModifierDefinition(
                Id: "lt",
                Sources: ["gamepad.leftShoulder"]
            ),
            new BindingModifierDefinition(
                Id: "rt",
                Sources: ["gamepad.rightShoulder"]
            ),
        ],
        Version: BindingProfileDocument.CurrentVersion
    );

    [Fact]
    public void AChordRowCarriesNoComputedMemberOnTheWire() {
        // IsResting and Members are derived from Chord/Held; emitting them would put a value on the wire that no
        // constructor parameter reads back.
        var json = JsonSerializer.Serialize(
            jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument,
            value: Document()
        );

        using var parsed = JsonDocument.Parse(json: json);

        var offenders = new List<string>();

        foreach (var row in parsed.RootElement.GetProperty(propertyName: "chords").EnumerateArray()) {
            foreach (var member in row.EnumerateObject()) {
                if (!AllowedChordMembers.Contains(value: member.Name)) {
                    offenders.Add(item: member.Name);
                }
            }
        }

        Assert.Equal(actual: string.Join(separator: ", ", values: offenders), expected: string.Empty);
    }
    [Fact]
    public void AWrittenBindingDocumentReadsBackIdentically() {
        // The round trip the strict reader owes the canonical writer: what the engine writes, the engine reads —
        // no unmapped member, no lost value. A computed member on the wire fails this at the READ, by name.
        var document = Document();
        var written = JsonSerializer.Serialize(
            jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument,
            value: document
        );
        var reread = JsonSerializer.Deserialize(
            json: written,
            jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument
        );

        Assert.Equal(
            actual: JsonSerializer.Serialize(
                jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument,
                value: reread
            ),
            expected: written
        );
    }
}
