using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    private static string BasisPath() => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "puck.basis.frozen.json"
    );
    // Every CommandValue-shaped node anywhere in the shipped basis, found by SHAPE rather than by path: an authored
    // constant rides a binding page entry, a wheel ring entry, and anywhere else a command takes a value, and a walk
    // that enumerated the paths it knew about would keep passing as new ones appeared.
    private static IEnumerable<JsonNode> BasisCommandValues() {
        var pending = new Stack<JsonNode>();

        pending.Push(item: JsonNode.Parse(json: File.ReadAllText(path: BasisPath()))!);

        while (pending.Count > 0) {
            var node = pending.Pop();

            if (node is JsonArray array) {
                foreach (var item in array) {
                    if (item is { }) {
                        pending.Push(item: item);
                    }
                }

                continue;
            }

            if (node is not JsonObject json) {
                continue;
            }

            if (
                (json.Count == 2) &&
                json.ContainsKey(propertyName: "kind") &&
                (json[propertyName: "raw"] is JsonArray { Count: 4 })
            ) {
                yield return json;

                continue;
            }

            foreach (var (_, value) in json) {
                if (value is { }) {
                    pending.Push(item: value);
                }
            }
        }
    }
    // The `document` member of every bindingOverlays row of the shipped basis world, as authored. Held as
    // JsonElement rather than a parsed model so each caller decides which context reads it.
    private static IEnumerable<JsonElement> BasisBindingOverlays() {
        using var basis = JsonDocument.Parse(json: File.ReadAllText(path: BasisPath()));

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
    public void TheShippedBasisSpellsEveryCommandValueTheWayItsOwnWriterDoes() {
        // A checked-in document that the canonical writer would write back DIFFERENTLY rewrites itself on the first
        // save, turning someone's unrelated edit into a diff nobody made. CommandValue.Raw is a Vector4 of floats and
        // Utf8JsonWriter spells a whole float without a fractional part, so the authored `2.0` came back as `2`. The
        // expected text is re-derived from the converter here rather than restated, so this cannot drift from it.
        var converter = new CommandValueJsonConverter();
        var options = new JsonSerializerOptions();
        var values = 0;

        foreach (var authored in BasisCommandValues()) {
            var text = authored.ToJsonString();
            var reader = new Utf8JsonReader(jsonData: Encoding.UTF8.GetBytes(s: text));

            Assert.True(condition: reader.Read());

            var value = converter.Read(
                options: options,
                reader: ref reader,
                typeToConvert: typeof(CommandValue)
            );

            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(utf8Json: stream)) {
                converter.Write(
                    options: options,
                    value: value,
                    writer: writer
                );
            }

            Assert.Equal(actual: Encoding.UTF8.GetString(bytes: stream.ToArray()), expected: text);
            ++values;
        }

        // A walk that found nothing would pass every assertion above.
        Assert.True(condition: (values > 0));
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
