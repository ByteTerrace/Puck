using Puck.Abstractions.Documents;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A per-body row selector inside a <see cref="WorldRowAssignment"/>: each body's own value of a
/// <c>state.body</c> counter picks the row the case for that value names. A body whose register holds a value no case
/// of any selector names keeps the row the assignment's sequence gives it. The register is body state, so the choice is
/// the body's and goes where the body goes: a kit action or a rule's <c>setBodyState</c> moves the row, a replay moves
/// it at the same tick, a checkpoint restores it with the register, and a crossing to another world carries the
/// register with the body (a destination declaring the same register under the same kind selects by the carried value).
/// The look assignment's selectors change only what a body wears; the kit assignment's selectors change how it moves
/// and what it collides as, so a collider change is its own authored mapping and a look never changes a kit.</summary>
/// <param name="Name">The selector's name, unique within its assignment: the key a document importing another refines
/// or drops this selector by.</param>
/// <param name="BodyState">The <c>state.body</c> counter whose value selects.</param>
/// <param name="Cases">The value-to-row mapping, each value at most once.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRowSelector(
    [property: DocumentText("a row selector's name")] string Name,
    [property: DocumentText("a per-body slot name")] string BodyState,
    IReadOnlyList<WorldRowSelectorCase> Cases
) {
    /// <summary>Returns the row the first selector whose register holds a value one of its cases names picks: the one
    /// reading every caller of an assignment's selectors shares, so the server's selection, its preview of a
    /// replacement document and a work price agree on which selector wins.</summary>
    /// <typeparam name="TRegisters">The register reader's type, a struct so a per-body read allocates nothing.</typeparam>
    /// <param name="selectors">The selectors, in the order they are read.</param>
    /// <param name="registers">The reader of each selector's register.</param>
    /// <returns>The case's row name, or <see langword="null"/> when no selector's register holds a whole value a case
    /// names. A register holding a fraction matches no case.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selectors"/> is <see langword="null"/>.</exception>
    public static string? Match<TRegisters>(IReadOnlyList<WorldRowSelector> selectors, in TRegisters registers) where TRegisters : struct, IRegisters {
        ArgumentNullException.ThrowIfNull(argument: selectors);

        const long Fraction = ((1L << Puck.Maths.FixedQ4816.FractionBitCount) - 1L);

        for (var index = 0; (index < selectors.Count); index++) {
            if (!registers.TryRead(selector: index, raw: out var raw) || ((raw & Fraction) != 0L)) {
                continue;
            }

            var value = (raw >> Puck.Maths.FixedQ4816.FractionBitCount);
            var cases = selectors[index].Cases;

            for (var caseIndex = 0; (caseIndex < cases.Count); caseIndex++) {
                if (cases[caseIndex].Value == value) {
                    return cases[caseIndex].Row;
                }
            }
        }

        return null;
    }

    /// <summary>Reads the registers an assignment's selectors name, for <see cref="Match{TRegisters}"/>.</summary>
    public interface IRegisters {
        /// <summary>Reads one selector's register.</summary>
        /// <param name="selector">The selector's place in its assignment.</param>
        /// <param name="raw">The register's value as raw Q48.16 bits.</param>
        /// <returns><see langword="false"/> when the register cannot be read (undeclared, or not a counter), which
        /// passes the selector over.</returns>
        bool TryRead(int selector, out long raw);
    }
}
/// <summary>One case of a <see cref="WorldRowSelector"/>: the row a body's register value selects.</summary>
/// <param name="Value">The register value, a whole number; a counter holding a fraction matches no case.</param>
/// <param name="Row">The kit or look row name the value selects.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRowSelectorCase(long Value, [property: DocumentText("a kit or look row name")] string Row);
