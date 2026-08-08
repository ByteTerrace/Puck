namespace Puck.Scripting;

/// <summary>The addon ABI's output cell kind wire values (byte 0 of a 32-byte guest→host output cell). Pinned
/// independently of any consumer enum. <c>0</c> is deliberately unassigned: a zeroed ring decodes as malformed
/// rather than as a valid cell.</summary>
public enum AddonOutCellKind : byte {
    /// <summary>A command the host applies with no requested capability — the required capability is derived
    /// host-side from the cell's (channel, verb), never guest-supplied.</summary>
    Act = 1,

    /// <summary>A request to mint a handle over a subject, naming the capability mask requested.</summary>
    Ask = 2,
}
