namespace Puck.Input.Devices;

/// <summary>
/// A pairing-state transition decoded from a wireless receiver slot's non-state report: the receiver announces
/// when a controller joins or leaves the slot, out of band from the state-report stream.
/// </summary>
internal enum WirelessSlotEvent {
    /// <summary>The report carries no pairing-state transition (a state, status, or unknown report).</summary>
    None = 0,
    /// <summary>A controller paired into this slot (it will begin streaming state reports).</summary>
    Connected = 1,
    /// <summary>The controller left this slot (powered off or re-paired elsewhere); the slot is empty again.</summary>
    Disconnected = 2,
}
