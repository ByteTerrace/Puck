using System.Globalization;

namespace Puck.World;

/// <summary>The rows the engine itself declares on an owned identity's document, each a generated name
/// (<see cref="GeneratedName"/>) so that no row the identity's template or its author wrote can be one of them: the
/// seeded locomotion rate rows, the chat log and inbox, a remembered controller's slots, and the counter a keyed
/// append draws its next key from.</summary>
public static class WorldIdentityRows {
    /// <summary>Gets the row a seeded identity names for its movement-rate override, <c>identity$move-speed</c>.</summary>
    public static CellName MoveSpeed { get; } = CellName.Parse(candidate: GeneratedName.Join("identity", "move-speed"));
    /// <summary>Gets the row a seeded identity names for its turn-rate override, <c>identity$turn-speed</c>.</summary>
    public static CellName TurnSpeed { get; } = CellName.Parse(candidate: GeneratedName.Join("identity", "turn-speed"));
    /// <summary>Gets the bounded row <c>chat.inbox</c> declares for the messages an identity sent, <c>chat$log</c>.</summary>
    public static CellName ChatLog { get; } = CellName.Parse(candidate: GeneratedName.Join("chat", "log"));
    /// <summary>Gets the bounded row <c>chat.inbox</c> declares for the messages an identity received,
    /// <c>chat$inbox</c>.</summary>
    public static CellName ChatInbox { get; } = CellName.Parse(candidate: GeneratedName.Join("chat", "inbox"));

    /// <summary>Returns the row remembering which machine a controller last drove, <c>controller$&lt;key&gt;$machine</c>.</summary>
    /// <param name="device">The controller's persisted device identity.</param>
    /// <returns>The row name.</returns>
    public static CellName ControllerMachine(Guid device) => CellName.Parse(candidate: GeneratedName.Join("controller", DeviceKey(device: device), "machine"));
    /// <summary>Returns the row remembering which device slot a controller last took,
    /// <c>controller$&lt;key&gt;$device</c>.</summary>
    /// <param name="device">The controller's persisted device identity.</param>
    /// <returns>The row name.</returns>
    public static CellName ControllerDevice(Guid device) => CellName.Parse(candidate: GeneratedName.Join("controller", DeviceKey(device: device), "device"));
    /// <summary>Returns the counter row a keyed append to <paramref name="row"/> draws its next key from,
    /// <c>&lt;row&gt;$seq</c>.</summary>
    /// <param name="row">The row appended to.</param>
    /// <returns>The counter row's name.</returns>
    public static CellName Sequence(CellName row) => CellName.Parse(candidate: GeneratedName.Append(
        name: row.Value,
        part: "seq"
    ));

    private static string DeviceKey(Guid device) => device.ToString(
        format: "N",
        provider: CultureInfo.InvariantCulture
    );
}
