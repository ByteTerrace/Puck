using System.Buffers;
using System.Text;
using System.Text.Json;
using Puck.Assets;

namespace Puck.World;

/// <summary>A replica-tier document as it crosses between processes: every root member by its name and the digest of
/// its compact canonical bytes (<see cref="WorldDefinitionMembers"/>), with the bytes of a member carried inline only
/// when the recipient is unlikely to hold them. The recipient assembles the document over a document it already holds:
/// a member whose digest it holds is its own instance, an inline member is read from the manifest, and any other is
/// read from its content store or fetched by its digest. A recipient that installed the sender's package is therefore
/// sent the manifest and the members disclosure changed, however large the world.</summary>
/// <remarks>
/// <para>A member travels inline when it is small, or when it differs from the same member of the document the sender
/// loaded (its source): such a member is what disclosure and the running world made of it, no installed package holds
/// it, and its digest may have moved again before a fetch for it arrives. Every other member is stable content of the
/// sender's source, which the sender's fetch door answers by digest for as long as the recipient is delivered
/// it.</para>
/// <para>Nothing read from a manifest is trusted. A member's bytes must hash to the digest the manifest names, a
/// replaced member is read as untrusted input, and the assembled document is validated as a delivery
/// (<see cref="WorldDefinitionValidator.TryValidateDelivered"/>, or the change against the held document under the
/// same rules). The assembled document has no directory, so no path it names is opened.</para>
/// </remarks>
public static class WorldDocumentManifest {
    /// <summary>The schema a manifest names.</summary>
    public const string SchemaName = "puck.world.manifest.v1";
    /// <summary>The most bytes of a member the sender carries inline whether or not the recipient may hold it.</summary>
    public const int InlineBytes = 4096;
    /// <summary>The most members a manifest names.</summary>
    public const int MaximumMembers = 4096;
    /// <summary>The most bytes one member holds when it is fetched by its digest.</summary>
    public const int MaximumMemberBytes = WorldProjectionContent.MaxPrototypeBytes;
    /// <summary>The most bytes the members of one assembled document hold together.</summary>
    public const long MaximumDocumentBytes = (256L << 20);

    private static readonly JsonEncodedText SchemaProperty = JsonEncodedText.Encode(value: "schema");
    private static readonly JsonEncodedText MembersProperty = JsonEncodedText.Encode(value: "members");
    private static readonly JsonEncodedText NameProperty = JsonEncodedText.Encode(value: "name");
    private static readonly JsonEncodedText DigestProperty = JsonEncodedText.Encode(value: "digest");
    private static readonly JsonEncodedText ValueProperty = JsonEncodedText.Encode(value: "value");


    /// <summary>Encodes a document's manifest.</summary>
    /// <param name="definition">The document as its recipient is disclosed it.</param>
    /// <param name="source">The document the sender loaded, whose members a recipient that installed the sender's
    /// package holds; <see langword="null"/> carries every member inline.</param>
    /// <param name="previous">The preceding document this stream sent, or null at attach and across a cut.</param>
    /// <returns>The manifest's bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static byte[] Encode(WorldDefinition definition, WorldDefinition? source, WorldDefinition? previous = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var members = WorldDefinitionMembers.Of(definition, previous ?? source, out _);
        var stable = ((source is null) ? null : WorldDefinitionMembers.Of(definition: source));
        var delivered = previous is null ? null : WorldDefinitionMembers.Of(previous);
        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 4096);
        var inlineBytes = 0L;

        using (var writer = new Utf8JsonWriter(bufferWriter: buffer, options: new JsonWriterOptions { SkipValidation = true })) {
            writer.WriteStartObject();
            writer.WriteString(propertyName: SchemaProperty, value: SchemaName);
            writer.WriteStartArray(propertyName: MembersProperty);
            foreach (var member in members.Members.OrderBy(keySelector: static member => member.Name, comparer: StringComparer.Ordinal)) {
                writer.WriteStartObject();
                writer.WriteString(propertyName: NameProperty, value: member.Name);
                writer.WriteString(propertyName: DigestProperty, value: member.Digest);
                if (!Holds(delivered, member) && ((member.Value.Length <= InlineBytes) || !Holds(stable, member))) {
                    writer.WritePropertyName(propertyName: ValueProperty);
                    writer.WriteRawValue(skipInputValidation: true, utf8Json: member.Value.Span);
                    inlineBytes += member.Value.Length;
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        WorldProjectionWork.Count(kind: WorldProjectionWork.Manifests);
        WorldProjectionWork.Count(amount: buffer.WrittenCount, kind: WorldProjectionWork.ManifestBytes);
        WorldProjectionWork.Count(amount: inlineBytes, kind: WorldProjectionWork.ManifestInlineBytes);

        return buffer.WrittenSpan.ToArray();

        static bool Holds(WorldDefinitionMembers? basis, in WorldDefinitionMembers.Member member) =>
            (basis is not null) && basis.TryGet(member.Name, out var held) &&
            string.Equals(held.Digest, member.Digest, StringComparison.Ordinal);
    }

    /// <summary>Returns the function a recipient resolves a member's digest with: its own content store first, then
    /// the sender's fetch door, whose bytes are hashed against the digest and kept in the store.</summary>
    /// <param name="content">The store the sender's content is kept in, or <see langword="null"/> for the
    /// process's.</param>
    /// <param name="fetch">The sender's authorized fetch door, or <see langword="null"/> to fetch nothing.</param>
    /// <returns>The resolving function, which answers <see langword="null"/> for a digest neither holds.</returns>
    public static Func<ContentPin, int, byte[]?> Resolve(ContentAddressedStore? content, Func<ContentPin, byte[]?>? fetch) =>
        (pin, maximumBytes) => (WorldProjectionContent.Resolver.TryRead(content: out var bytes, fetch: fetch, held: content, maximumBytes: maximumBytes, pin: pin) ? bytes.ToArray() : null);

    /// <summary>Returns the bytes of the member of a document that a digest names: what a sender's fetch door answers
    /// a recipient that is currently delivered the document.</summary>
    /// <param name="definition">The document as the recipient is disclosed it.</param>
    /// <param name="pin">The requested digest.</param>
    /// <param name="value">The member's compact canonical bytes, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a member of the document has the digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryMember(WorldDefinition definition, ContentPin pin, out ReadOnlyMemory<byte> value) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var digest = pin.Hex;

        foreach (var member in WorldDefinitionMembers.Of(definition: definition).Members) {
            if (string.Equals(a: member.Digest, b: digest, comparisonType: StringComparison.Ordinal)) {
                value = member.Value;

                return true;
            }
        }

        value = default;

        return false;
    }

    /// <summary>Assembles the document a manifest names.</summary>
    /// <param name="manifest">The manifest's bytes.</param>
    /// <param name="basis">A document the recipient holds and has admitted, over which the manifest is read: the
    /// stream's previous document, or the recipient's own copy of the world; <see langword="null"/> assembles from the
    /// manifest's own bytes and what <paramref name="resolve"/> answers.</param>
    /// <param name="resolve">Answers a member's bytes from its digest and the most bytes it may hold, from the
    /// recipient's store or the sender's fetch door, or <see langword="null"/> when neither holds them. The bytes it
    /// answers hash to the digest.</param>
    /// <param name="held">Runs with the assembled document before it is validated, so content it names by pin can be
    /// fetched before its rows are checked; <see langword="null"/> to fetch nothing.</param>
    /// <param name="definition">The assembled, validated document, which has no directory, when this returns
    /// <see langword="true"/>.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the manifest assembled into a document this process may present.</returns>
    public static bool TryAssemble(ReadOnlySpan<byte> manifest, WorldDefinition? basis, Func<ContentPin, int, byte[]?>? resolve, Action<WorldDefinition>? held,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string reason) {
        definition = null;
        if (!TryRead(entries: out var entries, manifest: manifest, reason: out reason)) {
            return false;
        }

        var basisMembers = ((basis is null) ? null : WorldDefinitionMembers.Of(definition: basis));
        var replacements = new List<(string Name, ReadOnlyMemory<byte>? Value)>();
        var named = new HashSet<string>(comparer: StringComparer.Ordinal);
        var total = 0L;
        var heldCount = 0;
        var inlineCount = 0;
        var fetchedCount = 0;
        var fetchedBytes = 0L;

        foreach (var entry in entries) {
            _ = named.Add(item: entry.Name);
            if ((basisMembers is not null) && basisMembers.TryGet(member: out var own, name: entry.Name) && string.Equals(a: own.Digest, b: entry.Digest, comparisonType: StringComparison.Ordinal)) {
                total += own.Value.Length;
                heldCount++;
                // With no basis to replace in, the document is composed from every member's bytes.
                continue;
            }
            if (!ContentPin.TryParseHex(hex: entry.Digest, pin: out var pin)) {
                reason = $"the manifest's member '{entry.Name}' names no digest";

                return false;
            }

            ReadOnlyMemory<byte> bytes;

            if (entry.Value is { } inline) {
                if (ContentPin.Compute(content: inline.Span) != pin) {
                    reason = $"the manifest's member '{entry.Name}' does not hash to the digest it names";

                    return false;
                }
                bytes = inline;
                inlineCount++;
            } else if (resolve?.Invoke(arg1: pin, arg2: MaximumMemberBytes) is { } resolved) {
                bytes = resolved;
                fetchedCount++;
                fetchedBytes += resolved.Length;
            } else {
                reason = $"the document's member '{entry.Name}' ({pin}) is not held here and its sender did not answer it";

                return false;
            }
            total += bytes.Length;
            if (total > MaximumDocumentBytes) {
                reason = $"the manifest names a document past {MaximumDocumentBytes} bytes";

                return false;
            }
            replacements.Add(item: (entry.Name, bytes));
        }
        WorldProjectionWork.Count(amount: heldCount, kind: WorldProjectionWork.ManifestMembersHeld);
        WorldProjectionWork.Count(amount: inlineCount, kind: WorldProjectionWork.ManifestMembersInline);
        WorldProjectionWork.Count(amount: fetchedCount, kind: WorldProjectionWork.ManifestMembersFetched);
        WorldProjectionWork.Count(amount: fetchedBytes, kind: WorldProjectionWork.ManifestFetchedBytes);

        try {
            if (basis is null) {
                definition = WorldDefinitionSerialization.DeserializeDelivered(held: held, utf8Json: Compose(members: replacements));
                reason = string.Empty;

                return true;
            }
            foreach (var member in basisMembers!.Members) {
                if (!named.Contains(item: member.Name)) { replacements.Add(item: (member.Name, null)); }
            }

            var assembled = WorldDefinitionMembers.Replace(basis: basis, context: WorldJsonContext.Untrusted, documentDirectory: null, replacements: replacements);

            held?.Invoke(obj: assembled);
            // The basis was admitted, so only what the manifest changed is judged, as a delivery.
            if (!WorldDefinitionValidator.TryValidateChange(after: assembled, before: basis, compilation: out _, delivered: true, reason: out reason)) {
                return false;
            }
            definition = assembled;

            return true;
        } catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or JsonException or NotSupportedException or ArgumentException or FormatException) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            definition = null;

            return false;
        }
    }

    // One manifest entry: a member's name and digest, and its bytes when the manifest carries them.
    private readonly record struct Entry(string Name, string Digest, ReadOnlyMemory<byte>? Value);

    private static bool TryRead(ReadOnlySpan<byte> manifest, out List<Entry> entries, out string reason) {
        entries = [];
        try {
            using var document = JsonDocument.Parse(utf8Json: manifest.ToArray(), options: new JsonDocumentOptions { MaxDepth = 256 });
            var root = document.RootElement;

            if (
                (root.ValueKind != JsonValueKind.Object) ||
                !root.TryGetProperty(propertyName: "schema", value: out var schema) ||
                (schema.ValueKind != JsonValueKind.String) ||
                !schema.ValueEquals(text: SchemaName) ||
                !root.TryGetProperty(propertyName: "members", value: out var members) ||
                (members.ValueKind != JsonValueKind.Array)
            ) {
                reason = $"the document is not a {SchemaName} manifest";

                return false;
            }
            if (members.GetArrayLength() > MaximumMembers) {
                reason = $"the manifest names more than {MaximumMembers} members";

                return false;
            }

            var names = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var member in members.EnumerateArray()) {
                if (
                    (member.ValueKind != JsonValueKind.Object) ||
                    !member.TryGetProperty(propertyName: "name", value: out var name) ||
                    (name.ValueKind != JsonValueKind.String) ||
                    !member.TryGetProperty(propertyName: "digest", value: out var digest) ||
                    (digest.ValueKind != JsonValueKind.String) ||
                    (name.GetString() is not { Length: > 0 } memberName) ||
                    !names.Add(item: memberName)
                ) {
                    reason = "a manifest member needs one name, used once, and a digest";

                    return false;
                }
                entries.Add(item: new Entry(
                    Digest: digest.GetString()!,
                    Name: memberName,
                    Value: (member.TryGetProperty(propertyName: "value", value: out var value) ? Encoding.UTF8.GetBytes(s: value.GetRawText()) : null)
                ));
            }
        } catch (JsonException exception) {
            reason = $"the manifest is not JSON: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    // One JSON object from members' names and bytes: the document a recipient with no basis parses whole.
    private static byte[] Compose(List<(string Name, ReadOnlyMemory<byte>? Value)> members) {
        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 65536);

        using (var writer = new Utf8JsonWriter(bufferWriter: buffer, options: new JsonWriterOptions { SkipValidation = true })) {
            writer.WriteStartObject();
            foreach (var (name, value) in members) {
                if (value is { } bytes) {
                    writer.WritePropertyName(propertyName: name);
                    writer.WriteRawValue(skipInputValidation: true, utf8Json: bytes.Span);
                }
            }
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
