namespace Puck.Abstractions.Counting;

/// <summary>
/// The ordered list of kinds one owner declares, built by the declarations themselves: an owner holds one registry in
/// a static field written before its kinds and declares each kind through <see cref="Declare"/>, so a kind is named
/// once and the order a report lists them in is the order the owner declares them in. Kinds are static properties,
/// which the runtime initializes in textual order, so the registry is complete when the owner's type finishes
/// initializing, and the first read of <see cref="Kinds"/> closes it.
/// <para>
/// The invariant is that a report's kind list holds exactly the kinds the owner declared, each once, and never
/// changes after it is first read. <see cref="Declare"/> refuses a declaration made after the list was read, so a kind
/// cannot be added behind a <see cref="WorkCounterSet"/> that already took its kinds.
/// </para>
/// </summary>
public sealed class WorkKindRegistry {
    private readonly List<WorkKind> m_declared = [];
    private WorkKind[]? m_kinds;

    /// <summary>Gets the declared kinds in declaration order. The first read closes the registry.</summary>
    public ReadOnlySpan<WorkKind> Kinds {
        get {
            var kinds = Volatile.Read(location: ref m_kinds);

            if (kinds is null) {
                lock (m_declared) {
                    kinds = (m_kinds ??= m_declared.ToArray());
                }
            }

            return kinds;
        }
    }

    /// <summary>Creates a kind and appends it to the list a report lists.</summary>
    /// <param name="name">The dotted name; see <see cref="WorkKind"/>.</param>
    /// <param name="unit">The unit one step of the kind's count stands for.</param>
    /// <param name="workClass">What two readings of the kind may be expected to agree on.</param>
    /// <returns>The kind, for the owner to hold as a static property.</returns>
    /// <exception cref="InvalidOperationException">The kinds were already read, so the list is closed.</exception>
    public WorkKind Declare(string name, string unit, WorkClass workClass) =>
        Add(kind: new WorkKind(
            name: name,
            unit: unit,
            workClass: workClass
        ));
    /// <summary>Creates a peak kind (<see cref="WorkKind.IsPeak"/>) and appends it to the list a report lists.</summary>
    /// <param name="name">The dotted name; see <see cref="WorkKind"/>.</param>
    /// <param name="unit">The unit one step of the kind's count stands for.</param>
    /// <param name="workClass">What two readings of the kind may be expected to agree on.</param>
    /// <returns>The kind, for the owner to hold as a static property.</returns>
    /// <exception cref="InvalidOperationException">The kinds were already read, so the list is closed.</exception>
    public WorkKind DeclarePeak(string name, string unit, WorkClass workClass) =>
        Add(kind: new WorkKind(
            name: name,
            peak: true,
            unit: unit,
            workClass: workClass
        ));

    private WorkKind Add(WorkKind kind) {
        var name = kind.Name;

        lock (m_declared) {
            if (m_kinds is not null) {
                throw new InvalidOperationException(message: $"Work kind '{name}' was declared after its registry's kinds were read.");
            }

            m_declared.Add(item: kind);
        }

        return kind;
    }
}
