namespace Puck.Abstractions.Machines;

/// <summary>A normalized controller port, independent of the machine's video outputs.</summary>
public interface IMachineInputPort {
    /// <summary>Gets the image held for the next advance or submission.</summary>
    MachinePadState State { get; }

    /// <summary>Sets the image the next advance or submission starts with, and withdraws every change scheduled inside
    /// it. Queued execution captures it when the host submits the tick segment.</summary>
    /// <param name="state">The complete controller image, including released controls.</param>
    void SetState(in MachinePadState state);
    /// <summary>Schedules a change of the image inside the next advance or submission: the port holds
    /// <paramref name="state"/> from <paramref name="offsetTicks"/> into that segment on, and after it, so a press lands
    /// on the native frame it happened in. A change at or past the segment's end takes effect with the next segment. A
    /// port that keeps no time inside a segment holds the latest image for the whole of it, which is the default.</summary>
    /// <param name="state">The complete controller image from then on.</param>
    /// <param name="offsetTicks">The engine ticks from the segment's start; at least the port's last scheduled
    /// change.</param>
    void SetState(in MachinePadState state, ulong offsetTicks) => SetState(state: in state);
}
/// <summary>Optional controller ports. Other hardware input remains available through provider operations.</summary>
public interface IMachineInputPorts {
    /// <summary>Gets input ports by provider-owned name, stable for the runtime's lifetime.</summary>
    IReadOnlyDictionary<string, IMachineInputPort> InputPorts { get; }
}
