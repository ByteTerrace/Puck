using Puck.Abstractions.Gpu;

namespace Puck.Testing;

/// <summary>
/// A <see cref="GpuObjectNaming"/> that records every name a device applies instead of reaching a backend, so a law can
/// read which objects were named, and that nothing was when naming is off. Safe to call from any thread, since a
/// pipeline set builds on the thread pool.
/// </summary>
/// <param name="isEnabled">Whether naming is on.</param>
internal sealed class RecordingGpuObjectNaming(bool isEnabled) : GpuObjectNaming {
    private readonly List<(GpuObjectKind Kind, nint Handle, string Name)> m_applied = [];
    private readonly Lock m_gate = new();

    /// <inheritdoc/>
    public override bool IsEnabled => isEnabled;
    /// <summary>Gets a copy of every name applied, in the order applied.</summary>
    public IReadOnlyList<(GpuObjectKind Kind, nint Handle, string Name)> Applied {
        get {
            lock (m_gate) {
                return [.. m_applied];
            }
        }
    }

    /// <inheritdoc/>
    protected override void Apply(GpuObjectKind kind, nint handle, string name) {
        lock (m_gate) {
            m_applied.Add(item: (kind, handle, name));
        }
    }
}
