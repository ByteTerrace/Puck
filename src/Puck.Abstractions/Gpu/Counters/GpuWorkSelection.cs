namespace Puck.Abstractions.Gpu;

/// <summary>
/// Which passes and which counts of a node's GPU work a readout writes. A pass is selected by a predicate over its
/// label (<see cref="ForPass"/> builds the usual one, whole segments by <see cref="Matches"/>), and a count by a prefix
/// of its kind's name without the leading <c>gpu.</c> segment, so <c>draws</c> selects <c>draws</c> and
/// <c>draws.indirect</c>. A selection that names passes omits the work recorded outside every pass, since that work
/// belongs to none of them. <see cref="All"/> selects everything.
/// </summary>
public sealed class GpuWorkSelection {
    private const string Boundaries = "$:. ";
    private const string KindPrefix = "gpu.";

    private readonly bool[] m_columns;
    private readonly Func<string, bool>? m_pass;

    /// <summary>Initializes a new instance of the <see cref="GpuWorkSelection"/> class.</summary>
    /// <param name="pass">The predicate over pass labels, or <see langword="null"/> to select every pass.</param>
    /// <param name="counter">The count-name prefix to select, or <see langword="null"/> to select every count.</param>
    public GpuWorkSelection(Func<string, bool>? pass = null, string? counter = null) {
        m_columns = new bool[GpuWork.SubmissionColumnCount];
        m_pass = pass;

        var kinds = GpuWork.SubmissionKinds;

        for (var column = 0; (column < kinds.Length); column++) {
            m_columns[column] = ((counter is null) || Named(kind: kinds[column].Name).StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: Named(kind: counter)
            ));
        }

        Counter = counter;
    }

    /// <summary>Gets the selection that writes every pass and every count.</summary>
    public static GpuWorkSelection All { get; } = new();
    /// <summary>Gets the count-name prefix, or <see langword="null"/> when every count is selected.</summary>
    public string? Counter { get; }
    /// <summary>Gets whether the selection names passes, so work outside every pass is omitted.</summary>
    public bool NamesPasses =>
        (m_pass is not null);

    /// <summary>Creates a selection of the passes named <paramref name="label"/> or under it.</summary>
    /// <param name="label">The pass label to select, whole segments.</param>
    /// <param name="counter">The count-name prefix to select, or <see langword="null"/> to select every count.</param>
    /// <returns>The selection.</returns>
    public static GpuWorkSelection ForPass(string label, string? counter = null) =>
        new(
            counter: counter,
            pass: (name => Matches(
                filter: label,
                name: name
            ))
        );
    /// <summary>Indicates whether <paramref name="name"/> is <paramref name="filter"/> or sits under it: the name starts
    /// with the filter and the next character begins a new segment (<c>$</c>, <c>:</c>, <c>.</c> or a space).</summary>
    /// <param name="filter">The selecting name.</param>
    /// <param name="name">The name to test.</param>
    /// <returns>Whether the name is selected.</returns>
    public static bool Matches(string filter, string name) =>
        (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: filter
        ) && ((name.Length == filter.Length) || Boundaries.Contains(value: name[filter.Length])));
    /// <summary>Indicates whether the count in <paramref name="column"/> of <see cref="GpuWork.SubmissionKinds"/> is
    /// selected.</summary>
    /// <param name="column">The column.</param>
    /// <returns>Whether the count is selected.</returns>
    public bool IncludesColumn(int column) =>
        m_columns[column];
    /// <summary>Indicates whether a lifetime kind is selected by the count prefix.</summary>
    /// <param name="kind">The kind's full name.</param>
    /// <returns>Whether the lifetime count is selected.</returns>
    public bool IncludesLifetime(string kind) =>
        ((Counter is null) || Named(kind: kind).StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Named(kind: Counter)
        ));
    /// <summary>Indicates whether the pass labeled <paramref name="label"/> is selected.</summary>
    /// <param name="label">The pass label.</param>
    /// <returns>Whether the pass is selected.</returns>
    public bool IncludesPass(string label) =>
        ((m_pass is null) || m_pass(arg: label));
    /// <summary>Indicates whether the prefix selects any submission or lifetime count.</summary>
    /// <returns>Whether at least one count is selected.</returns>
    public bool SelectsAnyCount() {
        if (Array.IndexOf(
            array: m_columns,
            value: true
        ) >= 0) {
            return true;
        }

        foreach (var kind in GpuWork.LifetimeKinds) {
            if (IncludesLifetime(kind: kind.Name)) {
                return true;
            }
        }

        return false;
    }

    // A kind's name without its leading "gpu." segment, as the text form prints it.
    private static ReadOnlySpan<char> Named(string kind) =>
        (kind.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: KindPrefix
        )
            ? kind.AsSpan(start: KindPrefix.Length)
            : kind.AsSpan()
        );
}
