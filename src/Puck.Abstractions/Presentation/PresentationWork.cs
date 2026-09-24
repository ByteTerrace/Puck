using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Presentation;

/// <summary>
/// A presenter's work counters, as one <see cref="IWorkCounterSource"/>. A backend's presenter owns one and records
/// each frame it skipped: submitted no GPU work for because the swapchain was not ready this tick (the backend retries
/// on the next). A skip is not an error. A presenter that never skips owns none.
/// </summary>
/// <param name="name">The source's name, <c>presentation.&lt;backend&gt;</c>.</param>
public sealed class PresentationWork(string name) : IWorkCounterSource {
    private static readonly WorkKind[] Kinds = [new(name: "presentation.skipped", unit: "count", workClass: WorkClass.Pacing)];

    private WorkCount m_skipped;

    /// <summary>Gets the kind counting presents a backend skipped: <c>presentation.skipped</c>.</summary>
    public static WorkKind Skipped =>
        Kinds[0];
    /// <inheritdoc/>
    public string Name { get; } = WorkKind.RequireSourceName(
        name: name,
        paramName: nameof(name)
    );
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds =>
        Kinds;

    /// <summary>Records one skipped present, from the presenter's single presenting thread.</summary>
    public void RecordSkip() =>
        m_skipped.Increment();
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) =>
        WorkCounterSources.TryReadSingle(
            count: in m_skipped,
            declared: Skipped,
            kind: kind,
            value: out value
        );
}
