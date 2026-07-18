namespace Puck.Scripting;

/// <summary>Specifies why an addon is disabled — a sticky, deterministic fault classification.</summary>
public enum AddonFaultKind {
    /// <summary>No fault; the addon is healthy.</summary>
    None = 0,

    /// <summary>The guest reported an ABI version the host does not speak.</summary>
    AbiMismatch,

    /// <summary>A required export is missing, has the wrong signature, or declares out-of-range regions.</summary>
    BadExport,

    /// <summary>A returned command record violated the fixed-stride decode guards.</summary>
    DecodeError,

    /// <summary>The tick exhausted its fuel budget and trapped deterministically.</summary>
    OutOfFuel,

    /// <summary>The guest exceeded the configured execution stack ceiling.</summary>
    StackOverflow,

    /// <summary>The guest accessed linear memory out of bounds.</summary>
    MemoryOutOfBounds,

    /// <summary>The guest executed an <c>unreachable</c> instruction.</summary>
    Unreachable,

    /// <summary>The guest trapped for any other reason.</summary>
    Trap,
}
