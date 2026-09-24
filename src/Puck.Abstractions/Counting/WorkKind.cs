namespace Puck.Abstractions.Counting;

/// <summary>
/// Names one kind of deterministic work a <see cref="IWorkCounterSource"/> can count — a dispatch, a probe, a leased
/// element. The owner of a kind declares it once as a static instance, and every reader compares kinds by reference,
/// so two kinds that happen to share a name are still two different kinds.
/// <para>
/// A name is dotted lowercase segments, most general first (<c>gpu.dispatches.indirect</c>,
/// <c>state.arena.change-window-probes</c>); a segment is letters and digits, optionally joined by single hyphens.
/// A unit is one such segment (<c>count</c>, <c>bytes</c>, <c>elements</c>).
/// </para>
/// <para>
/// The owner also declares the kind's <see cref="WorkClass"/>: what two readings of it at the same code over the same
/// workload may be expected to agree on, which is the only thing a collector compares.
/// </para>
/// </summary>
public sealed class WorkKind {
    /// <summary>Gets what two readings of this kind may be expected to agree on.</summary>
    public WorkClass Class { get; }
    /// <summary>Gets the dotted name a report prints for this kind.</summary>
    public string Name { get; }
    /// <summary>Gets the unit one step of this kind's count stands for.</summary>
    public string Unit { get; }

    /// <summary>Initializes a new instance of the <see cref="WorkKind"/> class.</summary>
    /// <param name="name">The dotted name: at least two segments, each lowercase letters and digits joined by single hyphens.</param>
    /// <param name="unit">The unit: one segment of the same form.</param>
    /// <param name="workClass">What two readings of the kind may be expected to agree on; never
    /// <see cref="WorkClass.AllocationZeroNonzero"/>, which classes an allocation reading rather than a count.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="unit"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="unit"/> is not of the stated form.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workClass"/> is not a class a count can have.</exception>
    public WorkKind(string name, string unit, WorkClass workClass) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(unit);

        if (!IsName(text: name)) {
            throw new ArgumentException(
                message: $"Work kind name '{name}' must be at least two dotted lowercase segments, the owner's area first, each letters and digits joined by single hyphens.",
                paramName: nameof(name)
            );
        }

        if (!IsSegment(text: unit)) {
            throw new ArgumentException(
                message: $"Work kind unit '{unit}' must be one lowercase segment of letters and digits joined by single hyphens.",
                paramName: nameof(unit)
            );
        }

        if (workClass is not (WorkClass.Deterministic or WorkClass.PerBackendDeterministic or WorkClass.Pacing)) {
            throw new ArgumentOutOfRangeException(
                actualValue: workClass,
                message: $"Work kind '{name}' must be deterministic, per-backend-deterministic or pacing; {workClass} classes an allocation reading, not a count.",
                paramName: nameof(workClass)
            );
        }

        Class = workClass;
        Name = name;
        Unit = unit;
    }

    /// <summary>Determines whether a text is a dotted name of the form a kind's <see cref="Name"/> and an
    /// <see cref="IWorkCounterSource.Name"/> take: at least two segments, each lowercase letters and digits joined by
    /// single hyphens.</summary>
    /// <param name="text">The candidate name.</param>
    /// <returns><see langword="true"/> when <paramref name="text"/> is of that form.</returns>
    public static bool IsName(ReadOnlySpan<char> text) {
        var segments = 0;

        foreach (var segment in text.Split(separator: '.')) {
            if (!IsSegment(text: text[segment])) {
                return false;
            }

            segments++;
        }

        return (segments >= 2);
    }
    /// <summary>Returns <paramref name="name"/> when it is a dotted name <see cref="IsName"/> accepts, the check every
    /// <see cref="IWorkCounterSource.Name"/> passes at construction.</summary>
    /// <param name="name">The candidate source name.</param>
    /// <param name="paramName">The name of the caller's parameter that carried <paramref name="name"/>.</param>
    /// <returns><paramref name="name"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a dotted work name.</exception>
    public static string RequireSourceName(string name, string paramName) {
        ArgumentNullException.ThrowIfNull(
            argument: name,
            paramName: paramName
        );

        if (!IsName(text: name)) {
            throw new ArgumentException(
                message: $"Work source name '{name}' must be at least two dotted lowercase segments, each letters and digits joined by single hyphens.",
                paramName: paramName
            );
        }

        return name;
    }
    /// <summary>Returns <see cref="Name"/>.</summary>
    /// <returns>The dotted name.</returns>
    public override string ToString() =>
        Name;

    private static bool IsSegment(ReadOnlySpan<char> text) {
        if (text.IsEmpty || (text[0] == '-') || (text[^1] == '-')) {
            return false;
        }

        for (var index = 0; (index < text.Length); index++) {
            var character = text[index];

            if (character == '-') {
                if (text[(index - 1)] == '-') {
                    return false;
                }

                continue;
            }

            if (!(char.IsAsciiLetterLower(c: character) || char.IsAsciiDigit(c: character))) {
                return false;
            }
        }

        return true;
    }
}
