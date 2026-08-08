using Puck.Input;

namespace Puck.World;

/// <summary>
/// Fans one raw window input event out to every registered <see cref="IWindowInputObserver"/> — the single
/// instance <see cref="WorldBootComposition"/> contributes as the <c>IWindowInputObserver</c> capability, since
/// <c>IHostContext.HoldsCapability</c> resolves exactly one instance per capability type and two observers (the
/// camera-orbit sink, the console text sink) both need every raw event.
/// </summary>
internal sealed class WorldWindowInputObservers : IWindowInputObserver {
    private readonly IWindowInputObserver[] m_observers;

    /// <summary>Initializes a new instance of the <see cref="WorldWindowInputObservers"/> class.</summary>
    /// <param name="observers">The observers to fan every event out to, invoked in this order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="observers"/> is <see langword="null"/>.</exception>
    public WorldWindowInputObservers(IEnumerable<IWindowInputObserver> observers) {
        ArgumentNullException.ThrowIfNull(argument: observers);

        m_observers = [.. observers];
    }

    /// <inheritdoc/>
    public void Observe(in WindowInputEvent inputEvent) {
        for (var index = 0; (index < m_observers.Length); index++) {
            m_observers[index].Observe(inputEvent: in inputEvent);
        }
    }
}
