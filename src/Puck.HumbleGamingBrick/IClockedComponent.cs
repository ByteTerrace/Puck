namespace Puck.HumbleGamingBrick;

/// <summary>
/// A hardware component the <see cref="SystemBus"/> advances in lockstep with the CPU. Every component speaks
/// only this vocabulary plus the register reads/writes the bus routes to it; nothing reaches across to another
/// component directly. The bus advances each component by a whole number of T-cycles (dots) per CPU machine
/// cycle, choosing the count from the component's <see cref="Domain"/> so that double-speed mode — where the
/// CPU clock doubles but the LCD clock does not — needs no change to the components themselves.
/// </summary>
public interface IClockedComponent {
    /// <summary>The clock domain this component is wired to, which determines how many T-cycles it advances
    /// per CPU machine cycle.</summary>
    ClockDomain Domain { get; }

    /// <summary>Advances the component by <paramref name="tCycles"/> T-cycles (dots).</summary>
    /// <param name="tCycles">The number of T-cycles to advance; always positive.</param>
    void Step(int tCycles);
}
