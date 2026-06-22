namespace Puck.Commands;

/// <summary>
/// The seam a <see cref="CommandRegistry"/> routes a <see cref="CommandRouting.Simulation"/> command to when it is
/// submitted as text: a pre-resolved <see cref="CommandInjection"/> is handed off here instead of being run inline.
/// The live capture point — the <see cref="InputRouter"/> — implements it, folding the injection into the per-tick
/// <see cref="CommandSnapshot"/>. Modeling it as an interface keeps the registry depending only on the abstraction,
/// not on the router (which already depends on the registry), so there is no construction cycle.
/// </summary>
public interface ICommandInjectionSink {
    /// <summary>Queues a pre-resolved command for the deterministic input path. Thread-safe.</summary>
    /// <param name="injection">The pre-resolved command to fold into a future tick's snapshot.</param>
    void Inject(in CommandInjection injection);
}
