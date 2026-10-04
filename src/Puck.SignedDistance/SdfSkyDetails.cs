namespace Puck.SignedDistance;

/// <summary>
/// The work-counter detail rows the sky, composite and environment passes count into (<c>puckCountDetail</c>): the field
/// runs' rows (<c>run0</c>, the lowest field run, then <c>run1</c> and <c>run2</c>, the upper ones), the composite's
/// <c>atmosphere</c> row (<see cref="AtmosphereRow"/>), where it counts one evaluation for each atmosphere kind it
/// evaluates at a pixel, then one row a layer label, in the order labels are first packed. A layer's record names its row (<see cref="SdfSkyLayer.Detail"/>). The
/// rows only grow, so a node's detail identities never move while its graph lives: a label a sky stops drawing keeps its
/// row and counts zero. Past <see cref="Capacity"/> rows every new label shares the last, <see cref="Overflow"/>.
/// One set of rows serves every residency of a composition (<c>SdfWorldPipelineCatalog</c>), so a view that follows
/// another residency keeps its rows. Safe on any thread.
/// </summary>
public sealed class SdfSkyDetails {
    /// <summary>The rows the field runs count in, ahead of every layer's: the lowest field run's, then
    /// <see cref="SdfSky.MaxUpperFieldRuns"/> upper runs'.</summary>
    public const int Runs = (1 + SdfSky.MaxUpperFieldRuns);
    /// <summary>The row the composite counts the atmosphere's evaluations in, after the runs'.</summary>
    public const int AtmosphereRow = Runs;
    /// <summary>The label of <see cref="AtmosphereRow"/>.</summary>
    public const string Atmosphere = "atmosphere";
    /// <summary>The rows every set holds ahead of the layers': the runs' and the atmosphere's.</summary>
    public const int Fixed = (Runs + 1);
    /// <summary>The most rows: the runs' and the layers', the last of them <see cref="Overflow"/>.</summary>
    public const int Capacity = 32;
    /// <summary>The label every layer shares once the rows are full.</summary>
    public const string Overflow = "other";

    private readonly Dictionary<string, uint> m_rows = new(comparer: StringComparer.Ordinal);
    private readonly Lock m_gate = new();

    private string[] m_labels;

    /// <summary>Initializes a new instance of the <see cref="SdfSkyDetails"/> class holding the fixed rows alone: the
    /// runs' and the atmosphere's.</summary>
    public SdfSkyDetails() {
        m_labels = [.. Enumerable.Range(count: Runs, start: 0).Select(selector: static run => RunLabel(run: run)), Atmosphere];

        for (var row = 0; (row < Fixed); row++) {
            m_rows.Add(key: m_labels[row], value: ((uint)row));
        }
    }

    /// <summary>Gets the rows' labels in row order. The array is never modified: a new label publishes a longer one.</summary>
    public IReadOnlyList<string> Labels => Volatile.Read(location: ref m_labels);

    /// <summary>Returns a field run's label.</summary>
    /// <param name="run">The run's row: zero for the lowest field run, then the upper runs.</param>
    /// <returns>The label, <c>run</c> and the row.</returns>
    public static string RunLabel(int run) => $"run{run}";
    /// <summary>Returns whether a label names one of the fixed rows every set holds: a field run's or the atmosphere's,
    /// which no layer may take.</summary>
    /// <param name="label">The label.</param>
    /// <returns>Whether it is a fixed row's label.</returns>
    public static bool IsFixed(string label) {
        if (string.Equals(a: label, b: Atmosphere, comparisonType: StringComparison.Ordinal)) {
            return true;
        }

        for (var run = 0; (run < Runs); run++) {
            if (string.Equals(a: label, b: RunLabel(run: run), comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Returns the row a layer label counts in, adding it after the last row when it is new.</summary>
    /// <param name="label">The layer's label: its name, or its kind's.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ArgumentException"><paramref name="label"/> is empty or names a fixed row.</exception>
    public uint RowOf(string label) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: label);

        lock (m_gate) {
            if (m_rows.TryGetValue(key: label, value: out var row)) {
                if (row < Fixed) {
                    throw new ArgumentException(message: $"The sky's detail label '{label}' names a fixed row: a field run's or the atmosphere's.", paramName: nameof(label));
                }

                return row;
            }

            var labels = m_labels;

            if (labels.Length >= (Capacity - 1)) {
                if (!m_rows.TryGetValue(key: Overflow, value: out row)) {
                    row = ((uint)labels.Length);
                    m_rows.Add(key: Overflow, value: row);
                    Volatile.Write(location: ref m_labels, value: [.. labels, Overflow]);
                }

                return row;
            }

            row = ((uint)labels.Length);
            m_rows.Add(key: label, value: row);
            Volatile.Write(location: ref m_labels, value: [.. labels, label]);

            return row;
        }
    }
}
