using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets;

namespace Puck.World;

/// <summary>A definition's root members as its compact canonical bytes slice them: each member's name, its value's bytes
/// and their digest, and the document's revision pin, a digest over the members' names and digests. A document change is
/// read member by member from these slices: two definitions whose member digests agree hold the same member, so a delta
/// between them names only the members whose digests moved and parses nothing else.</summary>
/// <remarks>A definition's slices are cut once, from one serialization, and kept beside it for as long as the
/// definition lives (<see cref="Of"/>). A definition built from another by replacing members
/// (<see cref="Replace"/>) is held with the slices it was built from, so its own are never cut again. A member the
/// serializer omits (a null value) has no slice; its absence is part of the pin.</remarks>
public sealed class WorldDefinitionMembers {
    private static readonly ConditionalWeakTable<WorldDefinition, WorldDefinitionMembers> Held = new();

    /// <summary>The JSON name of the root member holding the state section.</summary>
    public const string StateMemberName = "state";

    private readonly Dictionary<string, Member> m_members;

    /// <summary>One root member: its JSON name, the compact canonical bytes of its value, and their digest.</summary>
    /// <param name="Name">The member's JSON name.</param>
    /// <param name="Value">The compact canonical bytes of its value.</param>
    /// <param name="Digest">The <see cref="ContentPin.Hex"/> of <paramref name="Value"/>.</param>
    public readonly record struct Member(string Name, ReadOnlyMemory<byte> Value, string Digest) {
        /// <summary>Returns a member measured from its value's bytes.</summary>
        /// <param name="name">The member's JSON name.</param>
        /// <param name="value">The compact canonical bytes of its value.</param>
        /// <returns>The member with its digest.</returns>
        public static Member Of(string name, ReadOnlyMemory<byte> value) => new(Digest: ContentPin.Compute(content: value.Span).Hex, Name: name, Value: value);
        /// <summary>Parses the member's value.</summary>
        /// <returns>A fresh tree of the value.</returns>
        public JsonNode? Tree() => JsonNode.Parse(utf8Json: Value.Span);
    }

    private WorldDefinitionMembers(IEnumerable<Member> members) {
        m_members = new Dictionary<string, Member>(comparer: StringComparer.Ordinal);
        foreach (var member in members) { m_members[member.Name] = member; }
        Pin = PinOf(members: m_members.Values);
    }

    /// <summary>Gets the revision pin: a chain content pin (<see cref="WorldDefinitionFileSource.ComputeChainContentHash"/>) over every present member's name and digest, in ordinal name order, so
    /// it depends on what the document holds and never on the order its members were sliced in.</summary>
    public string Pin { get; }
    /// <summary>Gets the present members, in no particular order.</summary>
    public IReadOnlyCollection<Member> Members => m_members.Values;

    /// <summary>Returns the definition's member slices, cutting them from one compact canonical serialization the first
    /// time and keeping them beside the definition after.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>Its members.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldDefinitionMembers Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Held.GetValue(key: definition, createValueCallback: static definition => Slice(json: WorldDefinitionSerialization.SerializeCompact(definition: definition)));
    }
    /// <summary>Returns whether a root member name is one of the document model's members; any other name is an
    /// extension member (<see cref="WorldDefinition.Extensions"/>: a <c>_</c> comment or a <c>$</c> key), whose value is
    /// its <see cref="JsonElement"/>.</summary>
    /// <param name="name">The member's JSON name.</param>
    /// <returns><see langword="true"/> for a model member.</returns>
    public static bool IsModelMember(string name) => (WorldDocumentWalker.MemberType(name: name, owner: typeof(WorldDefinition)) is not null);
    /// <summary>Reads one root member's typed value as the member's bytes hold it: a model member through
    /// <see cref="WorldDocumentWalker.ReadMember"/>, an extension member as its <see cref="JsonElement"/>. The one read
    /// every member-wise change takes, so a document carrying extension members slices, deltas and reassembles like any
    /// other, and a value carried from one definition into another is what reading its slice gives.</summary>
    /// <remarks>A state row a preview sampler drew from a private stream serializes as the input it was installed from
    /// (the serializer writes <see cref="WorldStateRow.InstallationInput"/>), so the state member reads with each such row
    /// as that input: a change never carries a preview's words into another document.</remarks>
    /// <param name="definition">The definition.</param>
    /// <param name="name">The member's JSON name.</param>
    /// <returns>The value, or <see langword="null"/> when the definition does not hold it.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static object? Value(WorldDefinition definition, string name) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: name);

        if (!IsModelMember(name: name)) {
            return (((definition.Extensions is { } extensions) && extensions.TryGetValue(key: name, value: out var element)) ? element : null);
        }

        var value = WorldDocumentWalker.ReadMember(name: name, root: definition);

        if ((value is WorldStateSection { World: { } rows } state) && rows.Any(predicate: static row => !ReferenceEquals(objA: row.InstallationInput, objB: row))) {
            return (state with { World = [.. rows.Select(selector: static row => row.InstallationInput)] });
        }
        return value;
    }
    /// <summary>Returns whether the definition holds a member, and its slice.</summary>
    /// <param name="name">The member's JSON name.</param>
    /// <param name="member">The member's slice.</param>
    /// <returns>Whether the member is present.</returns>
    public bool TryGet(string name, out Member member) => m_members.TryGetValue(key: name, value: out member);

    /// <summary>Returns <paramref name="basis"/> with each named member replaced by the value its bytes hold (a
    /// <see langword="null"/> value removes the member), holding the result beside the slices it was built from:
    /// <paramref name="basis"/>'s for every member left alone and the given bytes for each replaced one.</summary>
    /// <remarks>Each replaced value is read with its declared type and then shares, through
    /// <see cref="WorldDocumentWalker.Share{T}"/>, every member and keyed row of <paramref name="basis"/>'s value the
    /// change leaves equal, so a product kept by a row's identity carries over. A replaced <c>state</c> section is set
    /// through <see cref="WorldDefinition.WithStateSection"/>, which keeps a compatible compiled state catalog. State
    /// references in the replaced values are resolved (<see cref="WorldStateDocumentValues"/>) as a parse resolves
    /// them.</remarks>
    /// <param name="basis">The definition the members are replaced in.</param>
    /// <param name="replacements">Each member's JSON name and its new value's compact canonical bytes, or
    /// <see langword="null"/> to remove it.</param>
    /// <param name="documentDirectory">The directory the result's relative paths resolve beside.</param>
    /// <param name="basisTrees">The tree of each replaced member's value in <paramref name="basis"/>, as
    /// <see cref="Write"/> writes it, for a caller that already holds them; a member absent here is read from
    /// <paramref name="basis"/>'s slices, or written afresh when it has none.</param>
    /// <returns>The replaced definition.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">A value does not read as its member's type, or a state reference in it
    /// does not resolve.</exception>
    public static WorldDefinition Replace(WorldDefinition basis, IReadOnlyList<(string Name, ReadOnlyMemory<byte>? Value)> replacements, string? documentDirectory,
        IReadOnlyDictionary<string, JsonNode?>? basisTrees = null) {
        ArgumentNullException.ThrowIfNull(argument: basis);
        ArgumentNullException.ThrowIfNull(argument: replacements);

        if (replacements.Count == 0) {
            return (string.Equals(a: basis.DocumentDirectory, b: documentDirectory, comparisonType: StringComparison.Ordinal) ? basis : (basis with { DocumentDirectory = documentDirectory }));
        }

        // Slices are kept for the result only when the basis's are, so a held table always covers every member.
        var held = (Held.TryGetValue(key: basis, value: out var members) ? members : null);
        var values = new Dictionary<string, object?>(comparer: StringComparer.Ordinal);
        var slices = new Dictionary<string, Member>(comparer: StringComparer.Ordinal);

        foreach (var member in (held?.Members ?? [])) { slices[member.Name] = member; }
        foreach (var (name, value) in replacements) {
            var current = Value(definition: basis, name: name);

            if (value is not { } bytes) {
                values[name] = null;
                _ = slices.Remove(key: name);
                continue;
            }

            var tree = JsonNode.Parse(utf8Json: bytes.Span)!;
            var before = (((basisTrees is not null) && basisTrees.TryGetValue(key: name, value: out var given))
                ? given
                : ((held is not null) ? (held.TryGet(name: name, member: out var slice) ? slice.Tree() : null) : Write(name: name, value: current)));

            slices[name] = Member.Of(name: name, value: bytes);
            // A value equal to the basis's is the basis's own instance.
            if ((before is not null) && (current is not null) && Puck.Abstractions.Documents.OrderedJson.Equals(left: before, right: tree)) {
                values[name] = current;
                continue;
            }

            var read = Read(name: name, value: bytes.Span);

            values[name] = (((before is not null) && (current is not null) && (read is not null) && IsModelMember(name: name))
                ? WorldDocumentWalker.Share(basis: current, basisTree: before, patch: WorldDocumentBasis.DiffValue(basis: before, target: tree)!, value: read, valueTree: tree)
                : read);
        }

        var replaced = Assemble(basis: basis, documentDirectory: documentDirectory, values: new Dictionary<string, object?>(dictionary: values, comparer: StringComparer.Ordinal));
        // Only the values read here are resolved, against the assembled document's shades and state: every other
        // member is the basis's own, resolved (or bound live) as the basis holds it.
        var rebound = new Dictionary<string, object?>(comparer: StringComparer.Ordinal);

        foreach (var (name, value) in values) {
            // An extension member is JSON the author keeps, with no state reference to resolve.
            if ((value is null) || !IsModelMember(name: name) || ReferenceEquals(objA: value, objB: WorldDocumentWalker.ReadMember(name: name, root: basis))) { continue; }
            if (!WorldStateDocumentValues.TryResolveGraph(graph: value, reason: out var reason, resolved: out var resolved, source: replaced)) {
                throw new InvalidDataException(message: reason);
            }
            if (!ReferenceEquals(objA: resolved, objB: value)) { rebound[name] = resolved; }
        }
        if (rebound.Count > 0) { replaced = Assemble(basis: replaced, documentDirectory: documentDirectory, values: rebound); }
        if (held is not null) { Held.AddOrUpdate(key: replaced, value: new WorldDefinitionMembers(members: slices.Values)); }
        return replaced;
    }
    /// <summary>Returns <paramref name="basis"/> with each named member holding a typed value already resolved, held
    /// beside <paramref name="slices"/> when given: the slices the result serializes to, which the caller vouches for
    /// (the values were read from, or share every row with, a document whose slices those are).</summary>
    /// <param name="basis">The definition the members are replaced in.</param>
    /// <param name="values">Each member's JSON name and the typed value it holds in the result.</param>
    /// <param name="documentDirectory">The directory the result's relative paths resolve beside.</param>
    /// <param name="slices">The result's member slices, or <see langword="null"/> to leave them to be cut when asked.</param>
    /// <returns>The replaced definition; <paramref name="basis"/> itself when nothing is replaced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="basis"/> or <paramref name="values"/> is
    /// <see langword="null"/>.</exception>
    public static WorldDefinition With(WorldDefinition basis, IReadOnlyDictionary<string, object?> values, string? documentDirectory, WorldDefinitionMembers? slices = null) {
        ArgumentNullException.ThrowIfNull(argument: basis);
        ArgumentNullException.ThrowIfNull(argument: values);

        var replaced = Assemble(basis: basis, documentDirectory: documentDirectory, values: new Dictionary<string, object?>(collection: values, comparer: StringComparer.Ordinal));

        if ((slices is not null) && !ReferenceEquals(objA: replaced, objB: basis)) { Held.AddOrUpdate(key: replaced, value: slices); }
        return replaced;
    }
    // The basis with each member set, the state section through WithStateSection so a compatible compiled catalog
    // carries over, and the directory set.
    private static WorldDefinition Assemble(WorldDefinition basis, Dictionary<string, object?> values, string? documentDirectory) {
        var replacesState = values.Remove(key: StateMemberName, value: out var state);
        Dictionary<string, JsonElement>? extensions = null;

        foreach (var name in values.Keys.Where(predicate: static name => !IsModelMember(name: name)).ToArray()) {
            // A fresh table: the basis's own is shared with every definition built from it.
            extensions ??= ((basis.Extensions is { } held) ? new Dictionary<string, JsonElement>(collection: held, comparer: StringComparer.Ordinal) : new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal));
            _ = values.Remove(key: name, value: out var value);
            if (value is JsonElement element) { extensions[name] = element; } else { _ = extensions.Remove(key: name); }
        }

        var replaced = WorldDocumentWalker.WithMembers(members: values, root: basis);

        if (extensions is not null) { replaced = (replaced with { Extensions = ((extensions.Count == 0) ? null : extensions) }); }
        if (replacesState) { replaced = replaced.WithStateSection(state: (WorldStateSection?)state); }
        return (string.Equals(a: replaced.DocumentDirectory, b: documentDirectory, comparisonType: StringComparison.Ordinal)
            ? replaced
            : (replaced with { DocumentDirectory = documentDirectory }));
    }
    /// <summary>Reads one root member's value with its declared type.</summary>
    /// <param name="name">The member's JSON name.</param>
    /// <param name="value">The value's JSON bytes.</param>
    /// <returns>The typed value.</returns>
    /// <exception cref="InvalidDataException">No root member has the name, or the value does not read as its type.</exception>
    public static object? Read(string name, ReadOnlySpan<byte> value) {
        var type = WorldDocumentWalker.MemberType(name: name, owner: typeof(WorldDefinition));

        try {
            if (type is null) {
                return ((DocumentExtensionsPolicy.IsReservedKey(key: name))
                    ? JsonSerializer.Deserialize<JsonElement>(utf8Json: value)
                    : throw new InvalidDataException(message: $"a {WorldDefinition.SchemaVersion} document has no root member '{name}'"));
            }
            return JsonSerializer.Deserialize(utf8Json: value, jsonTypeInfo: WorldJsonContext.Authored.Options.GetTypeInfo(type: type));
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            throw new InvalidDataException(message: $"the document member '{name}' is not valid: {WorldJsonPayload.Reason(exception: exception)}", innerException: exception);
        }
    }
    /// <summary>Writes one root member's typed value as a tree, as the document's serializer writes it.</summary>
    /// <param name="name">The member's JSON name.</param>
    /// <param name="value">The typed value.</param>
    /// <returns>The value's tree, or <see langword="null"/> for a null value.</returns>
    /// <exception cref="InvalidDataException">No root member has the name.</exception>
    public static JsonNode? Write(string name, object? value) {
        if (value is null) { return null; }
        if (value is JsonElement element) { return JsonNode.Parse(json: element.GetRawText()); }

        var type = (WorldDocumentWalker.MemberType(name: name, owner: typeof(WorldDefinition))
            ?? throw new InvalidDataException(message: $"a {WorldDefinition.SchemaVersion} document has no root member '{name}'"));

        return JsonSerializer.SerializeToNode(value: value, jsonTypeInfo: WorldJsonContext.Default.Options.GetTypeInfo(type: type));
    }

    // The members of one compact canonical document.
    private static WorldDefinitionMembers Slice(byte[] json) {
        var members = (WorldDefinitionFileSource.MembersOf(json: json)
            ?? throw new InvalidDataException(message: "a serialized definition is not one JSON object"));
        var memory = json.AsMemory();

        return new WorldDefinitionMembers(members: members.Select(selector: member => Member.Of(name: member.Name, value: memory.Slice(start: member.Start, length: member.Length))));
    }
    // The digest over every member's name and digest, in ordinal name order, through the length-delimited chain pin a
    // composed source's content pin folds its files with.
    private static string PinOf(IEnumerable<Member> members) => WorldDefinitionFileSource.ComputeChainContentHash(chain: [.. members
        .OrderBy(keySelector: static member => member.Name, comparer: StringComparer.Ordinal)
        .SelectMany(selector: static member => new[] { Encoding.UTF8.GetBytes(s: member.Name), Encoding.UTF8.GetBytes(s: member.Digest) })]);
}
