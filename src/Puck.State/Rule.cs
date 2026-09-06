using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>
/// One rule: a condition over state facts and the effects that follow. There is no scheduler and no trigger taxonomy
/// beside this: time is just another fact.
/// </summary>
/// <remarks>
/// <para><b>Addressing is a (row, key) pair.</b> A <c>setState</c>/<c>addState</c>/<c>compareState</c> names the row
/// in <c>State</c> and the cell in <c>Key</c>; a null key means the row's slot cell, which a keyed row does not have
/// and is refused for rather than silently reading the row's first cell. A read operand (a gate subject, a comparand,
/// a <c>fromState</c>) must additionally address a cell the row declares — an undeclared cell would read 0 forever
/// with no refusal anywhere, so it refuses at compile as <see cref="RuleRefusal.StateCellUndeclared"/>; write
/// destinations mint their cells and stay exempt.</para>
/// <para><b>Every N ticks is a moving threshold against <c>$tick</c>.</b> Gate <c>$tick &gt;= nextBeat</c> against an
/// <c>int</c> schedule row the rule's own effect advances by N on fire (<c>addState nextBeat += N</c>),
/// <see cref="ActionTriggerMode.Edge"/>. The advance lands synchronously inside the same evaluation pass, so for
/// N &gt;= 2 the gate self-closes the tick after it opens. Edge's latch — armed the instant the gate opened, before
/// the effect that was to close it ever ran — stops the runaway re-fire <see cref="ActionTriggerMode.Level"/> would
/// spam if the advance were ever denied. A period of exactly 1 tick never closes its own gate and wants Level.</para>
/// <para><b>A cooldown is a relative countdown, not a <c>$tick</c> threshold.</b> A <c>nextAllowed</c> row set to
/// <c>$tick</c>+N on use is open the instant a request arrives once background ticks have accrued. Build a cooldown as
/// a <c>NonNegative</c> <c>int</c> row a <see cref="ActionTriggerMode.Level"/> rule gated <c>&gt; 0</c> consumes each
/// tick with <see cref="ActionEffect.CountdownState"/>, and the ability gated on <c>&lt;= 0</c>; using the ability
/// re-arms it with <c>setState valueSeconds=N</c>.</para>
/// <para><b>A copy operand reads the same same-tick state a gate does</b>, so an earlier rule's write is visible to a
/// later rule's copy — declaration order decides it, deterministically.</para>
/// </remarks>
/// <param name="Name">The rule's stable name — unique within the section. A <see cref="CellName"/>, the same
/// validated-identifier type a state row and a cell key ride: dot-free and free of the reserved character set,
/// refused by name at the JSON converter. The reserved <c>$</c> prefix is refused on top of that, by
/// <see cref="RuleCompiler.CompileAll"/> — exactly as it is for a state row name, and for the same reason: <c>$</c>
/// marks what the engine mints, and nothing mints a rule.</param>
/// <param name="Effects">The effects applied in order when the rule fires.</param>
/// <param name="Gate">The predicate that must hold, or <see langword="null"/> for always.</param>
/// <param name="Mode">Whether the rule fires every tick the gate holds (<see cref="ActionTriggerMode.Level"/>, the
/// default) or once per crossing (<see cref="ActionTriggerMode.Edge"/>). A rule that writes a row almost always wants
/// <see cref="ActionTriggerMode.Edge"/>: level-firing an <c>addState</c> writes one journal entry per tick.</param>
/// <param name="ForEach">A keyed state row to iterate, <c>$zones</c> to iterate the rule's own
/// <paramref name="Zones"/> table (its non-empty indices, each bound to <c>$each</c>), or <see langword="null"/> for
/// one evaluation per tick. With a row named, the gate and effects evaluate once per cell the row holds at the top of the tick, with
/// <c>$each</c> bound to that cell's key — the quantifier that lets one rule tick a status for every participant
/// carrying it, or one rule judge every piece or card of a keyed row. An integer key also binds the <c>each</c>
/// participant reference; a non-integer key binds <c>$each</c> alone. The latch is kept per key, by the key's value
/// when it is an integer and by its position in the row otherwise.</param>
/// <param name="Bindings">The values bound once per evaluation, in declared order, read as <c>$bind:&lt;name&gt;</c>.</param>
/// <param name="Zones">The rule's zone table, or <see langword="null"/>: ordered zones over one token domain, in
/// index order, an empty entry holding an index no zone answers. Every row position in the rule — a
/// <c>compareState</c>'s <c>state</c>, a <c>$reduce:</c>/<c>$match:</c> row, a <c>$zone:</c> endpoint's zone, a
/// transfer's <c>from</c>/<c>to</c>, an expression's row — may spell <c>$zones[&lt;index&gt;]</c> to select an entry
/// live, the index being an infix cell key (<c>game[from]</c>, <c>$each</c>, <c>$bind:&lt;name&gt;</c>, or any
/// expression), so one rule serves every pile of a game. An evaluation applies only when every live index selects
/// an entry — an index outside the table, or at an empty entry, reads the gate closed, so a table's gaps are the
/// rule's own statement of which piles it is for. <c>forEach: "$zones"</c> iterates the table's own non-empty
/// indices with <c>$each</c> bound to each.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record Rule(
    [property: JsonPropertyOrder(0)] CellName Name,
    [property: JsonPropertyOrder(1)] IReadOnlyList<ActionEffect> Effects,
    [property: JsonPropertyOrder(2)][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Gate = null,
    [property: JsonPropertyOrder(3)] ActionTriggerMode Mode = ActionTriggerMode.Level,
    [property: JsonPropertyOrder(4)][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ForEach = null,
    [property: JsonPropertyOrder(6)][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RuleBinding>? Bindings = null,
    [property: JsonPropertyOrder(7)][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Zones = null
);
/// <summary>A value bound once per evaluation of the rule that declares it, after the forEach key and before the
/// gate, in declared order — a later binding, the gate, and every effect read it as
/// <c>$bind:&lt;name&gt;</c>; an earlier binding cannot. The value is never stored: it lives on the evaluation and is
/// recomputed at the next one.</summary>
/// <param name="Name">The binding's name — the token after <see cref="RuleFacts.BindPrefix"/>.</param>
/// <param name="Kind">The value's cell kind, <see cref="CellKind.Int"/> or <see cref="CellKind.Fixed"/>.</param>
/// <param name="Expression">The postfix expression, evaluated in that kind.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleBinding(CellName Name, CellKind Kind, ValueExpression Expression);
/// <summary>A name bound during one evaluation of a rule — the participant index a key token
/// <c>$each</c>/<c>$left</c>/<c>$right</c> or a participant-reference token <c>each</c>/<c>left</c>/<c>right</c>
/// reads, or the cell key a <c>$token</c> reads.</summary>
public enum BoundKey : byte {
    /// <summary>No binding — the literal key or index applies.</summary>
    None,

    /// <summary>The iterated cell key of a <see cref="Rule.ForEach"/> rule.</summary>
    Each,

    /// <summary>The left carrier of a pairwise evaluation.</summary>
    Left,

    /// <summary>The right carrier of a pairwise evaluation.</summary>
    Right,
    /// <summary>The token a pattern value expression is evaluating for — a cell key, never a participant.</summary>
    Token,
    /// <summary>The token before <see cref="Token"/> in the word a pattern's value expression walks; on the first token
    /// it names no cell, so a read through it is the absent cell.</summary>
    Previous,
}
/// <summary>The binding vocabulary: the key token and reference token each <see cref="BoundKey"/> spells, and the
/// scope it is live in.</summary>
public static class RuleBindingTokens {
    /// <summary>The binding vocabulary, one row per <see cref="BoundKey"/> other than <see cref="BoundKey.None"/>:
    /// the key token (<c>$each</c>, <c>$left</c>, <c>$right</c>), the reference token derived from it by
    /// <see cref="ReferenceTokenOf"/> (<c>each</c>, <c>left</c>, <c>right</c>), and the scope the binding is live in.
    /// Every switch and refusal that spells a binding reads this table.</summary>
    public static readonly (BoundKey Binding, string KeyToken, string Scope)[] Bindings = [
        (BoundKey.Each, "$each", "a rule declaring 'forEach'"),
        (BoundKey.Left, "$left", "an interaction or flock-affinity expression"),
        (BoundKey.Right, "$right", "a Distance interaction or flock-affinity expression"),
        (BoundKey.Token, "$token", "a pattern row's value expression, as the cell key of a row keyed over the zone's token domain"),
        (BoundKey.Previous, "$previous", "a pattern row's value expression, as the key of the token before the current one (the absent cell on the first)"),
    ];

    /// <summary>Returns the reference spelling of a binding's key token — the token without its leading <c>$</c>.</summary>
    /// <param name="keyToken">A <see cref="Bindings"/> key token.</param>
    public static string ReferenceTokenOf(string keyToken) => keyToken[1..];

    /// <summary>Returns the binding a key token spells, or <see cref="BoundKey.None"/>.</summary>
    /// <param name="key">The candidate key token.</param>
    public static BoundKey OfKeyToken(string? key) {
        foreach (var (binding, keyToken, _) in Bindings) {
            if (string.Equals(a: key, b: keyToken, comparisonType: StringComparison.Ordinal)) {
                return binding;
            }
        }

        return BoundKey.None;
    }

    /// <summary>Returns the binding a reference token spells, or <see cref="BoundKey.None"/>.</summary>
    /// <param name="token">The candidate reference token.</param>
    public static BoundKey OfReferenceToken(string token) {
        foreach (var (binding, keyToken, _) in Bindings) {
            if (string.Equals(a: token, b: ReferenceTokenOf(keyToken: keyToken), comparisonType: StringComparison.Ordinal)) {
                return binding;
            }
        }

        return BoundKey.None;
    }
}
/// <summary>Which symmetry-lattice map a <see cref="RuleFacts.SymmetryPrefix"/> operand applies to its source
/// node.</summary>
public enum SymmetryFunction : byte {
    /// <summary>The node's ring, 0..7.</summary>
    Ring,
    /// <summary>The antipodal node.</summary>
    Antipode,
    /// <summary>The smaller node of the antipodal pair — a stable unoriented-ray key.</summary>
    CanonicalRay,
    /// <summary>The node carried <c>argument</c> positions around its ring.</summary>
    Cycle,
    /// <summary>The node reflected through the other node's hyperplane.</summary>
    Reflect,
    /// <summary>1 when the node's ray and the other node's ray are orthogonal, else 0.</summary>
    Orthogonal,
    /// <summary>The exact inner product of the node and the other node as roots, in <c>-2..2</c>: 2 with itself, -2
    /// with its antipode, 1 for the 56 neighbours at sixty degrees, 0 for the orthogonal pairs.</summary>
    InnerProduct,
    /// <summary>The node's projected X coordinate, a fixed value.</summary>
    ProjectionX,
    /// <summary>The node's projected Y coordinate, a fixed value.</summary>
    ProjectionY,
}
/// <summary>What a <c>$match:</c> operand answers about its word.</summary>
public enum MatchFacet : byte {
    /// <summary>1 when the whole word is in the language, else 0.</summary>
    Accept,
    /// <summary>The length of the longest accepted prefix, or -1 when none is.</summary>
    Prefix,
    /// <summary>Over every direction of a board origin: bit d set when the ray in direction d is accepted.</summary>
    DirectionMask,
    /// <summary>Over every direction of a board origin: how many rays are accepted.</summary>
    DirectionCount,
    /// <summary>One board-origin ray: the cell one step past the longest accepted prefix — the first cell the
    /// pattern rejects — or -1 when the whole ray (to the edge or a wrapped return) is accepted.</summary>
    Cell,
    /// <summary>One board-origin ray: the step distance to <see cref="Cell"/>'s cell, or -1 on the same terms.</summary>
    Distance,
}
/// <summary>Hard bounds for rule programs; these are representation and per-tick work limits, not gameplay tuning.</summary>
public static class RuleCapacity {
    /// <summary>The most top-level effects one rule may carry.</summary>
    public const int MaxEffectsPerRule = 64;
    /// <summary>The most bound values one rule may declare — the width of the per-evaluation scratch every evaluator
    /// carries for them.</summary>
    public const int MaxBindingsPerRule = 16;
    /// <summary>The maximum statically derived rule work admitted for one simulation tick.</summary>
    public const long MaxWorkUnitsPerTick = 2_000_000L;
    /// <summary>The most postfix tokens in one Boolean gate.</summary>
    public const int MaxPredicateTokens = 256;
    /// <summary>The most postfix tokens in one numeric expression.</summary>
    public const int MaxExpressionTokens = 64;
    /// <summary>The most effects in one atomic transaction branch.</summary>
    public const int MaxTransactionEffects = 64;
}
