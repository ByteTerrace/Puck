using System.Numerics;
using Puck.Assets.Documents;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>How provider-neutral gamepad angular velocity projects into semantic camera look.</summary>
/// <param name="Scale">The dimensionless multiplier applied after projection.</param>
/// <param name="DeadZoneRaw">Independent X/Y/Z dead zones in radians per second. The threshold is removed from each
/// surviving magnitude so response remains continuous at its edge.</param>
/// <param name="InvertX">Whether the physical X angular-velocity axis is inverted before projection.</param>
/// <param name="InvertY">Whether the physical Y angular-velocity axis is inverted before projection.</param>
/// <param name="InvertZ">Whether the physical Z angular-velocity axis is inverted before projection.</param>
/// <param name="YawRaw">The X/Y/Z projection weights producing semantic look-right angular velocity.</param>
/// <param name="PitchRaw">The X/Y/Z projection weights producing semantic look-up angular velocity.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSeatGyro(
    float Scale = 1f,
    [property: JsonPropertyName("deadZone"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? DeadZoneRaw = null,
    bool InvertX = false,
    bool InvertY = false,
    bool InvertZ = false,
    [property: JsonPropertyName("yaw"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? YawRaw = null,
    [property: JsonPropertyName("pitch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? PitchRaw = null
) {
    private static float ApplyDeadZone(float value, float deadZone) {
        var magnitude = MathF.Abs(x: value);

        return ((magnitude <= deadZone)
            ? 0f
            : MathF.CopySign(
                x: (magnitude - deadZone),
                y: value
            )
        );
    }

    /// <summary>The default full-axis mapping: device pitch drives look pitch, while device yaw and roll both drive
    /// look yaw. Authors and player profiles may freely remap, combine, or invert all three axes.</summary>
    public static WorldSeatGyro Default { get; } = new();
    /// <summary>Independent X/Y/Z dead zones in radians per second.</summary>
    [JsonIgnore]
    public Vector3 DeadZone => (DeadZoneRaw ?? new Vector3(value: 0.02f));
    /// <summary>The X/Y/Z projection weights producing semantic look-up angular velocity.</summary>
    [JsonIgnore]
    public Vector3 Pitch => (PitchRaw ?? Vector3.UnitX);
    /// <summary>The X/Y/Z projection weights producing semantic look-right angular velocity.</summary>
    [JsonIgnore]
    public Vector3 Yaw => (YawRaw ?? new Vector3(
        x: 0f,
        y: -1f,
        z: -1f
    ));

    /// <summary>Projects provider-neutral angular velocity into semantic look-right/look-up radians per second,
    /// applying physical-axis dead zones and inversion before the authored full-axis projection.</summary>
    /// <param name="angularVelocity">The provider-neutral angular velocity in radians per second.</param>
    /// <returns>The semantic look-rate pair.</returns>
    public Vector2 Project(Vector3 angularVelocity) {
        var filtered = new Vector3(
            x: ApplyDeadZone(
                value: angularVelocity.X,
                deadZone: DeadZone.X
            ),
            y: ApplyDeadZone(
                value: angularVelocity.Y,
                deadZone: DeadZone.Y
            ),
            z: ApplyDeadZone(
                value: angularVelocity.Z,
                deadZone: DeadZone.Z
            )
        );

        filtered = new Vector3(
            x: (InvertX
            ? -filtered.X
            : filtered.X),
            y: (InvertY
            ? -filtered.Y
            : filtered.Y),
            z: (InvertZ
            ? -filtered.Z
            : filtered.Z)
        );

        return (new Vector2(
            x: Vector3.Dot(
                vector1: filtered,
                vector2: Yaw
            ),
            y: Vector3.Dot(
                vector1: filtered,
                vector2: Pitch
            )
        ) * Scale);
    }
}
/// <summary>
/// One seat's input feel — pointer/right-stick/gyro sensitivity and inversion. Camera structure does not
/// travel with a profile: the framing world's <see cref="WorldSeatViewControl"/> owns pitch limits and yaw reference.
/// </summary>
/// <remarks>Presentation-only. Nothing here rides a <c>CommandSnapshot</c> or feeds the deterministic simulation —
/// it shapes the local camera and nothing else, so a seat's feel can differ across two machines watching the same
/// world without their simulations diverging. ABSENT resolves to <see cref="Default"/>. What arms pointer drag or
/// toggles motion input is a binding, not a feel: <c>player.orbit</c>, <c>player.steer</c>, and
/// <c>player.motion.controls</c> are commands a page binds to whatever controls it likes.</remarks>
/// <param name="YawSensitivity">The yaw response in radians per pixel of raw pointer motion along the X axis.</param>
/// <param name="PitchSensitivity">The pitch response in radians per pixel of raw pointer motion along the Y axis.</param>
/// <param name="InvertYaw">Whether the final semantic yaw response (pointer, stick, and gyro) is inverted.</param>
/// <param name="InvertPitch">Whether the final semantic pitch response (pointer, stick, and gyro) is inverted.</param>
/// <param name="StickLookRate">The look stick's yaw/pitch rate in radians per second at full deflection.</param>
/// <param name="GyroRaw">The optional full-axis gyro projection. Absent resolves to <see cref="WorldSeatGyro.Default"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSeatLook(
    float YawSensitivity,
    float PitchSensitivity,
    bool InvertYaw,
    bool InvertPitch,
    float StickLookRate,
    [property: JsonPropertyName("gyro"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSeatGyro? GyroRaw = null
) {
    /// <summary>Gets the inert seat feel — zero pointer/stick response and the default gyro projection.</summary>
    public static WorldSeatLook Default { get; } = new(
        YawSensitivity: 0f,
        PitchSensitivity: 0f,
        InvertYaw: false,
        InvertPitch: false,
        StickLookRate: 0f
    );
    /// <summary>Gets the resolved full-axis gyro projection.</summary>
    [JsonIgnore]
    public WorldSeatGyro Gyro => (GyroRaw ?? WorldSeatGyro.Default);
}
