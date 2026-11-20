using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Puck.Abstractions.Documents;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>Replacements in an installed creation roster and edits to its shade table.</summary>
public sealed record WorldPresentationRows(WorldPresentationCreation[] Creations, WorldPresentationShade[] Shades, bool HasCreations, bool HasShades) {
    private byte[]? m_payload;
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool ChangesProjectionDependencies { get; init; }
    /// <summary>The document parts a presentation-row delivery can carry.</summary>
    public static WorldDocumentInputs Parts { get; } = WorldDocumentInputs.Of(WorldDocumentInput.Prototypes, WorldDocumentInput.Shades);

    /// <summary>Extracts the moved rows from two documents that share a creation roster.</summary>
    public static WorldPresentationRows Between(WorldDefinition before, WorldDefinition after) {
        var creations = new List<WorldPresentationCreation>();
        var projectionDependencies = false;
        if (!ReferenceEquals(before.CreationsRaw, after.CreationsRaw)) {
            for (var ordinal = 0; ordinal < after.Creations.Count; ordinal++) {
                var row = after.Creations[ordinal];
                var old = before.Creations[ordinal];
                if (ReferenceEquals(old, row)) { continue; }
                creations.Add(new(ordinal, row));
                projectionDependencies |= HasProjectionDependencies(old) || HasProjectionDependencies(row);
            }
        }
        var shades = new List<WorldPresentationShade>();
        if (!ReferenceEquals(before.ShadesRaw, after.ShadesRaw)) {
            if (after.ShadesRaw is { } next) {
                foreach (var (name, row) in next) {
                    if (before.ShadesRaw is not { } old || !old.TryGetValue(name, out var held) || !ReferenceEquals(held, row)) {
                        shades.Add(new(name, row));
                    }
                }
            }
            if (before.ShadesRaw is { } previous) {
                foreach (var name in previous.Keys) {
                    if (after.ShadesRaw?.ContainsKey(name) != true) { shades.Add(new(name, null)); }
                }
            }
        }
        shades.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return new([.. creations], [.. shades], after.CreationsRaw is not null, after.ShadesRaw is not null) {
            ChangesProjectionDependencies = projectionDependencies
        };
    }

    private static bool HasProjectionDependencies(WorldPrototype row) {
        var authored = WorldKeyedValues.Read(row);
        return authored.Bound.Count != 0 || authored.Keyed.Count != 0 || WorldStateDocumentValues.HasReference(row);
    }

    /// <summary>Applies a delivery to the same roster, preserving the recipient's other rows and state.</summary>
    public bool TryApply(WorldDefinition held, [NotNullWhen(true)] out WorldDefinition? changed) {
        changed = null;
        if ((!HasCreations && held.Creations.Count != 0) || (!HasShades && Shades.Any(static row => row.Definition is not null))) { return false; }
        var creations = held.CreationsRaw;
        if (Creations.Length != 0) {
            var next = held.Creations.ToArray();
            var last = -1;
            foreach (var (ordinal, row) in Creations) {
                if (ordinal <= last || (uint)ordinal >= (uint)next.Length || !next[ordinal].Id.Equals(row.Id)) { return false; }
                next[ordinal] = row;
                last = ordinal;
            }
            creations = next;
        }
        var shades = held.ShadesRaw;
        if (HasShades && Shades.Length != 0) {
            var next = shades is null ? new Dictionary<string, SdfShadeDefinition>(StringComparer.Ordinal) : new(shades, StringComparer.Ordinal);
            foreach (var (name, row) in Shades) {
                if (row is null) { next.Remove(name); } else { next[name] = row; }
            }
            shades = next;
        }
        changed = held with {
            CreationsRaw = HasCreations ? creations ?? [] : null,
            ShadesRaw = HasShades ? shades ?? new Dictionary<string, SdfShadeDefinition>(StringComparer.Ordinal) : null
        };
        return true;
    }

    /// <summary>Writes the compact row payload shared by replica deliveries.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReadOnlyMemory<byte> Payload => m_payload ??= CanonicalJsonDocument.SerializeCompact(this, WorldJsonContext.Default.For<WorldPresentationRows>());

    /// <summary>Reads a replica's row payload at the wire boundary.</summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out WorldPresentationRows? rows) {
        try {
            rows = JsonSerializer.Deserialize(payload, WorldJsonContext.Untrusted.For<WorldPresentationRows>());
            return rows is { Creations: not null, Shades: not null } &&
                Array.TrueForAll(rows.Creations, static row => row is { Row: not null }) &&
                Array.TrueForAll(rows.Shades, static row => row is { Name.Length: > 0 });
        } catch (Exception error) when (WorldJsonPayload.IsParseFailure(error)) {
            rows = null;
            return false;
        }
    }
}

/// <summary>A creation replaced at its existing roster ordinal.</summary>
public sealed record WorldPresentationCreation(int Ordinal, WorldPrototype Row);

/// <summary>A shade program to replace or remove by name.</summary>
public sealed record WorldPresentationShade(string Name, SdfShadeDefinition? Definition);
