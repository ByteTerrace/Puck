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

    // The `document` member of every bindingOverlays row of the shipped basis world, as authored. Held as
    // JsonElement rather than a parsed model so each caller decides which context reads it.
    private static IEnumerable<JsonElement> BasisBindingOverlays() {
        using var basis = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "puck.basis.frozen.json"
        )));

        foreach (var overlay in basis.RootElement.GetProperty(propertyName: "bindingOverlays").EnumerateArray()) {
            if (overlay.TryGetProperty(propertyName: "document", value: out var document)) {
                yield return document.Clone();
            }
        }
    }
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
    public void TheShippedBasisBindingsWriteTheSameBytesThroughBothContexts() {
        // The one-canonical-wire-shape claim, made on real authored data rather than a fixture: every binding
        // document the shipped basis world carries is read through the WORLD's context and written back through
        // BOTH — Puck.Commands' package-local BindingProfileJsonContext and the world document's own writer — and
        // the two must be the same bytes. They agree because neither context defines the shape: CommandValue,
        // ChannelRef, DocumentIdentifier and every binding enum declare their converter on the TYPE, so a future
        // edit that moved one back onto a context's converter list would break this by construction.
        var documents = 0;

        foreach (var overlay in BasisBindingOverlays()) {
            var document = overlay.Deserialize(jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument);

            Assert.NotNull(@object: document);
            Assert.Equal(
                actual: JsonSerializer.Serialize(
                    jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument,
                    value: document
                ),
                expected: JsonSerializer.Serialize(
                    jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument,
                    value: document
                )
            );
            ++documents;
        }

        // The fixture is only worth anything if the basis actually carries bindings; a silently empty walk would
        // pass every assertion above.
        Assert.True(condition: (documents > 0));
    }
    [Fact]
    public void TheShippedBasisBindingsReadBackThroughThePackageContextAlone() {
        // The consumer's path, with no world assembly anywhere near it: read the authored section through
        // Puck.Commands' own context, write it back, and the text is stable. This is what a Native AOT consumer
        // does; the World's context never touches it.
        foreach (var overlay in BasisBindingOverlays()) {
            var document = overlay.Deserialize(jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument);
            var written = JsonSerializer.Serialize(
                jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument,
                value: document
            );

            Assert.Equal(
                actual: JsonSerializer.Serialize(
                    jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument,
                    value: JsonSerializer.Deserialize(
                        json: written,
                        jsonTypeInfo: BindingProfileJsonContext.Default.BindingProfileDocument
                    )
                ),
                expected: written
            );
        }
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
