using System.Globalization;

namespace Puck.World;

/// <summary>Checks the consumed-format constraint of a prepared coupled presentation tuple. It runs only after
/// an input changes, and must not resolve authored fields or retain presentation state.</summary>
/// <param name="values">The ordered binary32 operands.</param>
/// <returns>Whether the derived operations remain in their admitted numeric domain.</returns>
public delegate bool WorldValueTuplePredicate(ReadOnlySpan<float> values);

public sealed partial class WorldValueDomainGuard {
    /// <summary>Checks one coupled shaping tuple only when its raw operands change. Scalar clamps and holds use
    /// the last presented tuple; only a candidate satisfying the derived constraint replaces that tuple.</summary>
    /// <param name="field">The authored row and ordered coupled field names.</param>
    /// <param name="source">The prepared source spellings in the same order.</param>
    /// <param name="values">The current binary32 operands.</param>
    /// <param name="initial">The admitted tick-zero operands.</param>
    /// <param name="predicate">The shared admission and runtime derived-domain predicate.</param>
    /// <param name="domain">The concrete author-facing numeric requirement.</param>
    /// <param name="operands">Optional scalar domains, checked atomically with the derived constraint.</param>
    /// <returns>The guard-owned valid operands, copied only after a changed valid input.</returns>
    public ReadOnlySpan<float> Tuple(string field, string source, scoped ReadOnlySpan<float> values, scoped ReadOnlySpan<float> initial,
        WorldValueTuplePredicate predicate, string domain, scoped ReadOnlySpan<WorldValueTupleOperand> operands = default) {
        ArgumentNullException.ThrowIfNull(predicate);
        if (values.IsEmpty || values.Length != initial.Length) {
            throw new ArgumentException("A presentation tuple requires matching nonempty live and admitted operands.", nameof(values));
        }
        if (!operands.IsEmpty && operands.Length != values.Length) {
            throw new ArgumentException("A presentation tuple requires one scalar domain per operand.", nameof(operands));
        }
        var entry = Get(field, initial[0], 0d);
        if (entry.RawTuple is null) {
            if (!predicate(initial)) { throw new ArgumentException($"The admitted presentation tuple '{field}' violates {domain}.", nameof(initial)); }
            for (var index = 0; index < operands.Length; index++) {
                if (!operands[index].Domain.Contains(initial[index])) {
                    throw new ArgumentException($"The admitted presentation tuple '{field}.{operands[index].Name}' violates {operands[index].Domain}.", nameof(initial));
                }
            }
            entry.RawTuple = new float[values.Length];
            entry.LastTuple = initial.ToArray();
        }
        if (entry.RawTuple.Length != values.Length) {
            throw new ArgumentException("A presentation tuple changes shape only with its structural revision.", nameof(values));
        }
        if (entry.Seen && values.SequenceEqual(entry.RawTuple)) { return entry.LastTuple; }
        Checks++;
        Span<float> candidate = stackalloc float[values.Length];
        values.CopyTo(candidate);
        var scalarInvalid = false;
        var scalarHold = false;
        var clamps = 0;
        for (var index = 0; index < operands.Length; index++) {
            var bounds = operands[index].Domain;
            if (bounds.Contains(values[index])) { continue; }
            scalarInvalid = true;
            if (bounds.RequiresHold(values[index])) {
                candidate[index] = entry.LastTuple![index];
                scalarHold = true;
            } else {
                candidate[index] = (float)Math.Clamp(values[index], bounds.Minimum, bounds.Maximum);
                clamps++;
            }
        }
        var derivedInvalid = !predicate(candidate);
        var invalid = scalarInvalid || derivedInvalid;
        var held = scalarHold || derivedInvalid;
        if (held) { Holds++; }
        if (!derivedInvalid) {
            candidate.CopyTo(entry.LastTuple);
            Clamps += clamps;
        }
        values.CopyTo(entry.RawTuple);
        entry.Seen = true;
        if (invalid || entry.Invalid) {
            Changed(entry, invalid, new(field, source, values[0], entry.LastTuple![0], domain,
                invalid ? (held ? "held" : "clamped") : "recovered", Values: TupleText(values), UsedValues: TupleText(entry.LastTuple)));
        }
        return entry.LastTuple;
    }

    private static string TupleText(ReadOnlySpan<float> values) {
        var items = new string[values.Length];
        for (var index = 0; index < values.Length; index++) { items[index] = values[index].ToString("R", CultureInfo.InvariantCulture); }
        return "[" + string.Join(", ", items) + "]";
    }
}
