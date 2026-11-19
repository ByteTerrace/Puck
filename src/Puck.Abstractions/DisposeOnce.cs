namespace Puck.Abstractions;

/// <summary>An <see cref="IDisposable"/> that runs an action on its first <see cref="Dispose"/> and does nothing on any
/// later one, from whichever thread disposes it first.</summary>
/// <param name="action">The action, or <see langword="null"/> for none.</param>
public sealed class DisposeOnce(Action? action) : IDisposable {
    private Action? m_action = action;

    /// <inheritdoc/>
    public void Dispose() => Interlocked.Exchange(location1: ref m_action, value: null)?.Invoke();
}
