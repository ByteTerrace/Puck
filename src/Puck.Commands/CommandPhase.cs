namespace Puck.Commands;

/// <summary>
/// Specifies the transition that a command activation represents.
/// </summary>
/// <remarks>
/// Continuous consumers typically poll the command value and ignore the phase, whereas discrete
/// consumers act on the edges represented by <see cref="Started"/> and <see cref="Completed"/>.
/// </remarks>
public enum CommandPhase {
    /// <summary>The first frame on which the command became active, such as a digital press or the start of an impulse.</summary>
    Started = 0,

    /// <summary>The command is held or continuously updated, such as an analog axis re-asserted each frame.</summary>
    Active,

    /// <summary>The command was released after being active, or a one-shot impulse completed within a single frame.</summary>
    Completed,

    /// <summary>The command was aborted before completing.</summary>
    Canceled,
}
