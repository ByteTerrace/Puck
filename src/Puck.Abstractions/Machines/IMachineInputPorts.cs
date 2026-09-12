namespace Puck.Abstractions.Machines;

/// <summary>A normalized controller port, independent of the machine's video outputs.</summary>
public interface IMachineInputPort {
    /// <summary>Gets the image held for the next advance or submission.</summary>
    MachinePadState State { get; }
    /// <summary>Sets the input image. Queued execution captures it when the host submits the tick segment.</summary>
    /// <param name="state">The complete controller image, including released controls.</param>
    void SetState(in MachinePadState state);
}

/// <summary>Optional controller ports. Other hardware input remains available through provider operations.</summary>
public interface IMachineInputPorts {
    /// <summary>Gets input ports by provider-owned name, stable for the runtime's lifetime.</summary>
    IReadOnlyDictionary<string, IMachineInputPort> InputPorts { get; }
}
