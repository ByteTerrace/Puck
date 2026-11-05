using Puck.Abstractions.Counting;

namespace Puck.DirectX;

/// <summary>Native queue work from the neutral Direct3D 12 submitter; presentation and host copies submit separately.</summary>
public sealed class DirectXSubmissionWork : IWorkCounterSource {
    private static readonly WorkKind Lists = new(name: "gpu.submission.lists", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    private static readonly WorkKind Executes = new(name: "gpu.submission.executes", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    private static readonly WorkKind Signals = new(name: "gpu.submission.signals", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    private static readonly WorkKind[] Kinds = [Lists, Executes, Signals];
    private readonly WorkCounterSet m_counts = new(name: "submission.directx", kinds: Kinds);

    /// <summary>Gets the process's submission counts across devices and recreations.</summary>
    public static DirectXSubmissionWork Process { get; } = new();
    /// <inheritdoc/>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => Kinds;
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) => m_counts.TryRead(kind, out value);
    internal void Execute(int lists) { m_counts.Add(Lists, lists); m_counts.Count(Executes); }
    internal void Signal() => m_counts.Count(Signals);
}
