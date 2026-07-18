namespace Puck.Input.Devices;

/// <summary>
/// Optional parser surface for controllers reached through a multi-slot wireless receiver: classifies the
/// receiver's out-of-band pairing reports so the device loop can wake a dormant slot (re-running initialization
/// for the freshly paired pad) and park it again on disconnect, instead of inferring pairing from stream
/// silence.
/// </summary>
internal interface IWirelessSlotParser {
    /// <summary>
    /// Classifies a report that was not a state report (<see cref="IGamepadParser.TryParse"/> returned
    /// <see langword="false"/>) as a pairing event, when it is one.
    /// </summary>
    /// <param name="report">The raw report bytes as read from the device.</param>
    /// <returns>The pairing transition the report announces, or <see cref="WirelessSlotEvent.None"/>.</returns>
    WirelessSlotEvent ClassifySlotEvent(ReadOnlySpan<byte> report);
}
