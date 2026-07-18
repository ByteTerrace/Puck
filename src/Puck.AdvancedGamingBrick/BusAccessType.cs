namespace Puck.AdvancedGamingBrick;

/// <summary>Specifies whether a bus access follows the previous one in address order, which selects the
/// wait-state the memory controller charges. The ARM7TDMI drives this on every access: a fetch or transfer to
/// the next address in sequence is sequential and may use the faster S cycle, while a branch, the first access
/// of a burst, or a change of direction is non-sequential and pays the slower N cycle.</summary>
public enum BusAccessType {
    /// <summary>A non-sequential access (N cycle): the address is unrelated to the previous one.</summary>
    NonSequential = 0,
    /// <summary>A sequential access (S cycle): the address immediately follows the previous one.</summary>
    Sequential = 1,
}
