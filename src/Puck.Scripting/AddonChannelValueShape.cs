namespace Puck.Scripting;

/// <summary>Specifies the SCALAR record value shape a resolved addon-ABI channel carries, resolved once at
/// handshake via <see cref="IAddonChannelResolver.TryResolve"/> against the host's own channel table — never
/// against anything the guest declares. Every shape rides a single lane (<c>A</c>); <c>B</c> and <c>C</c> are
/// always required-zero.</summary>
public enum AddonChannelValueShape {
    /// <summary>A two-sided analog value: <c>A</c> in <c>[-One, +One]</c>.</summary>
    Bipolar = 0,

    /// <summary>A pressed/released control. <c>A</c> is exactly <c>0</c> or <see cref="AddonAbi.One"/> — a
    /// fixed-point literal, never the old <c>{0, 1}</c> boolean convention.</summary>
    Binary = 1,

    /// <summary>A one-sided analog value: <c>A</c> in <c>[0, One]</c>. Pinned for a future channel; the interim
    /// host table declares none today.</summary>
    Unipolar = 2,
}
