using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Puck.Abstractions.Documents;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>The rows that moved in a definition a stream already holds: creations replaced in its roster, edits to its
/// shade table, placement rows replaced at their ordinals and placement holdings. A replica stream whose frame moved
/// with values alone is handed its moved placement rows here rather than its whole document
/// (<see cref="WorldDeliveryCursor.TryDeliverPlacements"/>).</summary>
public sealed record WorldPresentationRows(WorldPresentationCreation[] Creations, WorldPresentationShade[] Shades, bool HasCreations, bool HasShades) {
    private byte[]? m_payload;
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool ChangesProjectionDependencies { get; init; }
    /// <summary>The document parts a presentation-row delivery can carry.</summary>
    public static WorldDocumentInputs Parts { get; } = WorldDocumentInputs.Of(WorldDocumentInput.Prototypes, WorldDocumentInput.Shades);
    /// <summary>Gets the placement rows replaced at their ordinals, in ascending ordinal order, each keeping the id the
    /// held row has there.</summary>
    public WorldPresentationPlacement[] Placements { get; init; } = [];
    /// <summary>Gets the placement rows whose <see cref="WorldPlacement.Holding"/> alone moved, in ascending ordinal
    /// order: the holder applies them to its own rows (<see cref="WorldDefinition.WithPlacementHoldings"/>), which keeps
    /// every product compiled from them.</summary>
    public WorldPresentationHolding[] Holdings { get; init; } = [];
    /// <summary>Gets the number of rows the delivery carries.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Count => (Creations.Length + Shades.Length + Placements.Length + Holdings.Length);
    /// <summary>Gets whether the delivery moves a placement row.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MovesPlacements => ((Placements.Length + Holdings.Length) != 0);
    /// <summary>Gets whether the delivery moves placement holdings and nothing else.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MovesHoldingsOnly => ((Holdings.Length != 0) && (Creations.Length == 0) && (Shades.Length == 0) && (Placements.Length == 0));

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
        changed = held with { CreationsRaw = HasCreations ? creations ?? [] : null };
        // ShadesRaw reads the table the uses derive, so it is set only when the delivery moves it: setting it back
        // would replace the recipient's stored table with another instance, which reads as a shade change.
        var shades = held.ShadesRaw;
        if (!HasShades) {
            if (shades is not null) { changed = changed with { ShadesRaw = null }; }
        } else if (Shades.Length != 0) {
            var next = shades is null ? new Dictionary<string, SdfShadeDefinition>(StringComparer.Ordinal) : new(shades, StringComparer.Ordinal);
            foreach (var (name, row) in Shades) {
                if (row is null) { next.Remove(name); } else { next[name] = row; }
            }
            changed = changed with { ShadesRaw = next };
        } else if (shades is null) {
            changed = changed with { ShadesRaw = new Dictionary<string, SdfShadeDefinition>(StringComparer.Ordinal) };
        }
        return TryApplyPlacements(held: changed, changed: out changed);
    }
    // Applies the placement rows to a roster of the same rows: holdings to the holder's own rows, so the products
    // compiled from them carry over, then each replaced row at an ordinal whose held row has its id. False, applying
    // nothing, when an ordinal is out of order or names no row, or a replacement's id differs.
    private bool TryApplyPlacements(WorldDefinition held, [NotNullWhen(true)] out WorldDefinition? changed) {
        changed = held;
        if (!MovesPlacements) { return true; }
        var rows = held.Placements;
        var last = -1;
        foreach (var (ordinal, _) in Holdings) {
            if ((ordinal <= last) || ((uint)ordinal >= (uint)rows.Count)) { changed = null; return false; }
            last = ordinal;
        }
        last = -1;
        foreach (var (ordinal, row) in Placements) {
            if ((ordinal <= last) || ((uint)ordinal >= (uint)rows.Count) || !string.Equals(rows[ordinal].Id, row.Id, StringComparison.Ordinal)) { changed = null; return false; }
            last = ordinal;
        }
        if (Holdings.Length != 0) {
            var holdings = new (int Row, int Holding)[Holdings.Length];
            for (var index = 0; index < holdings.Length; index++) { holdings[index] = (Holdings[index].Ordinal, Holdings[index].Holding); }
            changed = changed.WithPlacementHoldings(holdings);
        }
        if (Placements.Length != 0) {
            var next = changed.Placements.ToArray();
            foreach (var (ordinal, row) in Placements) { next[ordinal] = row; }
            changed = changed with { PlacementRowsRaw = next };
        }
        return true;
    }

    /// <summary>Writes the compact row payload shared by replica deliveries.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReadOnlyMemory<byte> Payload => m_payload ??= CanonicalJsonDocument.SerializeCompact(this, WorldJsonContext.Default.For<WorldPresentationRows>());

    /// <summary>Reads a replica's row payload at the wire boundary. The wire carries creations and shades alone: a
    /// payload naming a placement row or holding is refused, since a placement crossing a process boundary arrives
    /// in a whole document, whose door resolves its state references.</summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out WorldPresentationRows? rows) {
        try {
            rows = JsonSerializer.Deserialize(payload, WorldJsonContext.Untrusted.For<WorldPresentationRows>());
            return rows is { Creations: not null, Shades: not null, Placements.Length: 0, Holdings.Length: 0 } &&
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

/// <summary>A placement row replaced at its existing ordinal.</summary>
public sealed record WorldPresentationPlacement(int Ordinal, WorldPlacement Row);

/// <summary>The new <see cref="WorldPlacement.Holding"/> of the placement row at an ordinal, the row otherwise as held.</summary>
public readonly record struct WorldPresentationHolding(int Ordinal, int Holding);
