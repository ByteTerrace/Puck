namespace Puck.Scripting;

/// <summary>Specifies an addon's lifecycle state on the sim-tick thread.</summary>
public enum AddonState {
    /// <summary>The addon instantiated cleanly and is driven every tick.</summary>
    Enabled = 0,

    /// <summary>The addon hit a sticky fault and is skipped until an explicit re-enable re-instantiates it.</summary>
    Faulted,

    /// <summary>The addon was administratively disabled and is skipped until re-enabled.</summary>
    Disabled,
}
