using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The authored seed identities and player presentation tuning. Every field is optional; the resolved
/// (non-"Raw") property of each field's own name states its ABSENT semantics. <see cref="SeatLook"/> is read per
/// seat from whichever document owns it: the world's for an unclaimed seat, the joined identity's own for a claimed
/// one, which is how a player's feel travels with their profile (see <see cref="WorldSeatLook"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlayerDefaults(
    // ABSENT semantics below hold for every raw field: the document declares only what it wants to state; a raw
    // field's resolved sibling property (same name, no "Raw" suffix) is what every consumer reads.
    [property: JsonPropertyName("identities"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldIdentitySeed>? IdentitiesRaw = null,
    [property: JsonPropertyName("neutralColor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NeutralColorRaw = null,
    [property: JsonPropertyName("colorSequence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSequence? ColorSequenceRaw = null,
    float Saturation = 0f,
    float Value = 0f,
    int ColorSearchLimit = 1,
    float NoseFactor = 0f,
    float PickerThreshold = 0f,
    [property: JsonPropertyName("pickerNeutralColor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PickerNeutralColorRaw = null,
    float PickerNeutralBlend = 0f,
    [property: JsonPropertyName("seatLook"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSeatLook? SeatLookRaw = null
) {
    /// <summary>The neutral gray used for every color field an absent document leaves unauthored.</summary>
    private const string InertColor = "#8C8C8C";

    /// <summary>Gets the identities used to seed an absent owned-world directory — ABSENT resolves to no seed
    /// identities.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldIdentitySeed> Identities => (IdentitiesRaw ?? []);
    /// <summary>Gets the placeholder color used when no profile identity is available — ABSENT resolves to a
    /// neutral gray.</summary>
    [JsonIgnore]
    public string NeutralColor => (NeutralColorRaw ?? InertColor);
    /// <summary>Gets the deterministic sequence used for generated profile colors — ABSENT resolves to the inert
    /// additive sequence.</summary>
    [JsonIgnore]
    public WorldSequence ColorSequence => (ColorSequenceRaw ?? WorldSequence.AdditiveDefault);
    /// <summary>Gets the pending-avatar desaturation target — ABSENT resolves to a neutral gray.</summary>
    [JsonIgnore]
    public string PickerNeutralColor => (PickerNeutralColorRaw ?? InertColor);
    /// <summary>Gets the control feel a seat of this document wakes with — ABSENT resolves to
    /// <see cref="WorldSeatLook.Default"/>.</summary>
    [JsonIgnore]
    public WorldSeatLook SeatLook => (SeatLookRaw ?? WorldSeatLook.Default);
    /// <summary>Gets the inert player-presentation defaults.</summary>
    public static WorldPlayerDefaults Default { get; } = new();
}
/// <summary>One authored identity used to seed an owned world.</summary>
/// <param name="Id">The stable profile id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Color">The body color as <c>#RRGGBB</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldIdentitySeed(WorldSafeName Id, string Name, string Color);
/// <summary>The person or character identity an owned world represents.</summary>
/// <param name="Id">The stable owned-world id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Color">The body color as <c>#RRGGBB</c>.</param>
/// <param name="MoveSpeedState">The fixed state row supplying locomotion speed.</param>
/// <param name="TurnSpeedState">The fixed state row supplying turn speed.</param>
/// <param name="Controllers">Machine/device state-slot references used for controller pre-selection.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldIdentityDefinition(WorldSafeName Id, string Name, string Color, WorldCellName MoveSpeedState, WorldCellName TurnSpeedState, IReadOnlyList<WorldControllerStateSlots>? Controllers = null);
/// <summary>Two text state rows that identify one reconnect-stable controller.</summary>
/// <param name="MachineState">The row containing the machine id.</param>
/// <param name="DeviceState">The row containing the device id.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldControllerStateSlots(WorldCellName MachineState, WorldCellName DeviceState);
