using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>A data-composable gate over named state. A rule fires only while its gate holds. The <c>$type</c> string
/// is the JSON discriminator, the same convention every polymorphic row family uses; the arms declared here are the
/// ones this library owns, and a document project appends its own derived arms through a <see cref="RuleVocabulary"/>
/// (see <see cref="RuleVocabulary.ExtendJson"/>) rather than by editing this list.</summary>
[JsonDerivedType(typeof(ActionPredicate.CompareState), typeDiscriminator: "compareState")]
[JsonDerivedType(typeof(ActionPredicate.CompareValue), typeDiscriminator: "compareValue")]
[JsonDerivedType(typeof(ActionPredicate.All), typeDiscriminator: "all")]
[JsonDerivedType(typeof(ActionPredicate.Any), typeDiscriminator: "any")]
[JsonDerivedType(typeof(ActionPredicate.Not), typeDiscriminator: "not")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionPredicate {
    /// <summary>Comparison of two bounded numeric expressions. Arithmetic failure makes this comparison false.</summary>
    /// <param name="Left">Left numeric expression.</param>
    /// <param name="Comparison">The comparison operation.</param>
    /// <param name="Right">Right numeric expression.</param>
    /// <param name="Kind">The common Int or Fixed domain; no implicit conversion is performed.</param>
    public sealed record CompareValue(ValueExpression Left, ActionStateComparison Comparison, ValueExpression Right, CellKind Kind = CellKind.Fixed) : ActionPredicate;
    /// <summary>Compares a named state cell against either a fixed authored value, or another named state
    /// cell/reserved channel read live at the same evaluation. Both spellings are authorable; exactly one of
    /// <paramref name="Value"/> and <paramref name="ComparandState"/> may be present (refused by name when both or
    /// neither are). The comparand-row spelling is what lets a gate track a moving threshold — <c>$tick</c> compared
    /// against a schedule row the rule's own effects advance is "every N ticks"; a round row compared against a
    /// declared length row is a round boundary — composition over the same two-sided comparison, never a new
    /// mechanism.</summary>
    /// <param name="State">A declared <c>state</c>-section row name, or one of the reserved channels the
    /// compiler's vocabulary answers (<see cref="RuleFacts"/> and whatever a document project registers). Inside a
    /// host's per-participant action program, a named counter slot the program declares.</param>
    /// <param name="Comparison">The comparison to apply.</param>
    /// <param name="Value">The authored constant comparand, or <see langword="null"/> when
    /// <paramref name="ComparandState"/> spells the comparand instead.</param>
    /// <param name="Key">The cell inside <paramref name="State"/> to read — <see langword="null"/> reads the row's
    /// slot cell, which a keyed row does not have (refused by name rather than silently reading <c>cells[0]</c>).</param>
    /// <param name="ComparandState">Another declared <c>state</c>-section row name, or a reserved channel, read live
    /// and compared instead of <paramref name="Value"/>. A dotted spelling (an author reaching for <c>row.key</c> in
    /// one string) is refused by name — address the cell with <paramref name="ComparandKey"/> instead. Comparing
    /// across incompatible cell kinds (an <c>int</c> row against a <c>fixed</c> row, say) is refused by name — mixing
    /// scales silently is worse than naming the mismatch.</param>
    /// <param name="ComparandKey">The cell inside <paramref name="ComparandState"/>, on the same (row, key) terms as
    /// <paramref name="Key"/>. Refused when <paramref name="ComparandState"/> names a reserved channel or is absent.</param>
    public sealed record CompareState(
        string State,
        ActionStateComparison Comparison,
        decimal? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandKey = null
    ) : ActionPredicate;
    /// <summary>Every inner predicate holds (conjunction).</summary>
    public sealed record All(IReadOnlyList<ActionPredicate> Predicates) : ActionPredicate;
    /// <summary>At least one inner predicate holds (disjunction). The list must be non-empty.</summary>
    /// <param name="Predicates">The non-empty child-predicate list.</param>
    public sealed record Any(IReadOnlyList<ActionPredicate> Predicates) : ActionPredicate;
    /// <summary>Inverts one predicate.</summary>
    /// <param name="Predicate">The child predicate to invert.</param>
    public sealed record Not(ActionPredicate Predicate) : ActionPredicate;
}
