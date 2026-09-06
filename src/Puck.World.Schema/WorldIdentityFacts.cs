using System.Globalization;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The facts an owned identity carries: one keyed <see cref="CellKind.Int"/> row on the identity's own
/// document, minted on the first write and bounded by an authored capacity, that travels with the identity into
/// every world declaring the <see cref="WorldIdentityFactLane"/>. A fact is a key and an integer and nothing more —
/// what a key means is the reading world's own rule to author.</summary>
/// <param name="State">The keyed <see cref="CellKind.Int"/> row on the identity's document holding the facts.</param>
/// <param name="Capacity">How many distinct facts the row holds; a write past it refuses by name.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldIdentityFacts(CellName State, int Capacity) {
    /// <summary>The shape an identity section authoring no <c>facts</c> member carries.</summary>
    public static WorldIdentityFacts Default { get; } = new(
        State: CellName.Parse(candidate: "identity-facts"),
        Capacity: 64
    );
}

/// <summary>The reserved body-scope lane a world declares when it reads facts: a keyed <see cref="CellKind.Int"/>
/// row named <see cref="RowName"/> in <c>state.world</c>, one cell per (body, fact) keyed
/// <c>&lt;bodyIndex&gt;-&lt;fact&gt;</c>. The server loads a body's lane from its identity when a seat binds one,
/// zeroes it when the seat unbinds, and every <c>setIdentityFact</c> effect writes the lane and the identity's own
/// row together. A world declaring no such row carries no facts and refuses every fact effect and operand by
/// name.</summary>
public static class WorldIdentityFactLane {
    /// <summary>The reserved row name.</summary>
    public const string RowName = "identity";
    /// <summary>The character between the body index and the fact in a lane key — the one character a
    /// <see cref="CellName"/> admits that a decimal body index never contains.</summary>
    public const char Separator = '-';

    /// <summary>Spells the lane key of one (body, fact) pair.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    /// <param name="fact">The fact key.</param>
    public static string Key(int bodyIndex, string fact) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{bodyIndex}{Separator}{fact}"
    );
    /// <summary>Splits a lane key into its body index and fact key.</summary>
    /// <param name="key">The lane key.</param>
    /// <param name="bodyIndex">The body index, on success.</param>
    /// <param name="fact">The fact key, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="key"/> spells <c>&lt;bodyIndex&gt;-&lt;fact&gt;</c>.</returns>
    public static bool TryParse(ReadOnlySpan<char> key, out int bodyIndex, out ReadOnlySpan<char> fact) {
        var separator = key.IndexOf(value: Separator);

        if (
            (separator > 0) &&
            (separator < (key.Length - 1)) &&
            int.TryParse(s: key[..separator], style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out bodyIndex)
        ) {
            fact = key[(separator + 1)..];

            return true;
        }

        bodyIndex = -1;
        fact = default;

        return false;
    }
    /// <summary>Returns whether a lane key belongs to <paramref name="bodyIndex"/>.</summary>
    /// <param name="key">The lane key.</param>
    /// <param name="bodyIndex">The body index.</param>
    public static bool BelongsTo(ReadOnlySpan<char> key, int bodyIndex) => (TryParse(key: key, bodyIndex: out var owner, fact: out _) && (owner == bodyIndex));
}
