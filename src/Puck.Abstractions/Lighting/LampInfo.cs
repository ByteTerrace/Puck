namespace Puck.Abstractions.Lighting;

/// <summary>
/// The immutable attributes of one lamp, as read from a HID LampArray's per-lamp attributes report. The
/// load-bearing fields for a bind legend are <see cref="Position"/> (where the lamp sits, for spatial effects)
/// and the input binding (<see cref="InputBindingUsagePage"/> / <see cref="InputBindingUsage"/>) — how a lamp
/// declares <em>which control it lights</em>, so a legend can color the key a command is bound to without a
/// per-device key map.
/// </summary>
/// <param name="LampId">The device-assigned id of the lamp (the value used to address it in an update report).</param>
/// <param name="Position">The lamp's normalized position within the array's bounding box.</param>
/// <param name="Purposes">The purposes the lamp declares.</param>
/// <param name="InputBindingUsagePage">
/// The HID usage page of the control this lamp lights (e.g. <c>0x07</c> Keyboard/Keypad), or <c>0</c> when the
/// lamp is not bound to a control. The HID report carries only the usage; the page is inferred from the device
/// kind (keyboard lamps bind keyboard usages).
/// </param>
/// <param name="InputBindingUsage">The HID usage of the control this lamp lights (e.g. a keyboard key usage), or <c>0</c> when unbound.</param>
/// <param name="RedLevelCount">The number of distinct red levels the lamp supports (2 for on/off, 256 for full 8-bit).</param>
/// <param name="GreenLevelCount">The number of distinct green levels the lamp supports.</param>
/// <param name="BlueLevelCount">The number of distinct blue levels the lamp supports.</param>
/// <param name="IntensityLevelCount">The number of distinct intensity levels the lamp supports.</param>
/// <param name="IsProgrammable">Whether the lamp can be individually addressed by an update report.</param>
/// <param name="UpdateLatencyInMilliseconds">The lamp's reported update latency, in milliseconds.</param>
public readonly record struct LampInfo(
    int LampId,
    LampPosition Position,
    LampPurposes Purposes,
    ushort InputBindingUsagePage,
    ushort InputBindingUsage,
    byte RedLevelCount,
    byte GreenLevelCount,
    byte BlueLevelCount,
    byte IntensityLevelCount,
    bool IsProgrammable,
    int UpdateLatencyInMilliseconds
) {
    /// <summary>Gets whether this lamp declares an input binding (it sits on a nameable control).</summary>
    public bool HasInputBinding => (InputBindingUsage != 0);
}
