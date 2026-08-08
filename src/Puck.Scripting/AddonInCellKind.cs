namespace Puck.Scripting;

/// <summary>The addon ABI's input cell kind wire values (byte 0 of a 32-byte host→guest input cell). Pinned
/// independently of any consumer enum. <c>0</c> is deliberately unassigned: a zeroed ring decodes as malformed
/// rather than as a valid cell.</summary>
public enum AddonInCellKind : byte {
    /// <summary>The single per-batch tick cell, always first: the engine tick, nothing else.</summary>
    Tick = 1,

    /// <summary>A response to one of the guest's previous-batch output cells, correlated by ordinal.</summary>
    Answer = 2,

    /// <summary>A host-pushed disclosure the guest did not request.</summary>
    Observation = 3,
}
