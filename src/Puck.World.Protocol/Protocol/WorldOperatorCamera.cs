using System.Globalization;
using System.Numerics;

namespace Puck.World.Protocol;

/// <summary>
/// The operator's free camera: one absolute eye pose that replaces what every window slot shows until it is cleared.
/// It is session presentation, never the document, and the sim never reads it (floats are fine here). A pose is an
/// eye, a heading and an elevation; the pivot a swing orbits is the point <see cref="Distance"/> metres ahead of the
/// eye, so a look-at pose orbits its target and an orbit pose orbits its pivot. Heading uses the placement
/// convention: yaw 0 faces -Z and a positive yaw turns left about +Y (yaw 90 faces -X, 180 faces +Z); pitch is
/// positive looking up.
/// </summary>
/// <param name="X">The eye's world X.</param>
/// <param name="Y">The eye's world Y.</param>
/// <param name="Z">The eye's world Z.</param>
/// <param name="YawRadians">The heading, wrapped to (-pi, pi].</param>
/// <param name="PitchRadians">The elevation, within <see cref="MaxPitchRadians"/>.</param>
/// <param name="FieldOfViewRadians">The vertical field of view.</param>
/// <param name="Distance">How far ahead of the eye the pivot lies, positive.</param>
public sealed record WorldOperatorCamera(float X, float Y, float Z, float YawRadians, float PitchRadians, float FieldOfViewRadians, float Distance) {
    /// <summary>The farthest the camera pitches either way: 89 degrees, short of the pole where "up" is undefined.</summary>
    public const float MaxPitchRadians = (89f * (MathF.PI / 180f));
    /// <summary>The field of view a pose takes when the operator names none: 60 degrees.</summary>
    public const float DefaultFieldOfViewRadians = (MathF.PI / 3f);
    /// <summary>The pivot distance of a pose that names no target: ten metres.</summary>
    public const float DefaultDistance = 10f;
    /// <summary>The nearest a swing may bring the eye to its pivot, in metres.</summary>
    public const float MinDistance = 0.05f;

    private const float DegreesToRadians = (MathF.PI / 180f);
    private const float RadiansToDegrees = (180f / MathF.PI);

    private static float Wrap(float radians) => (radians - (MathF.Tau * MathF.Round(x: (radians / MathF.Tau))));
    private static float ClampPitch(float radians) => Math.Clamp(
        value: radians,
        min: -MaxPitchRadians,
        max: MaxPitchRadians
    );
    private static Vector3 Heading(float yaw, float pitch) => new(
        x: (-MathF.Sin(x: yaw) * MathF.Cos(x: pitch)),
        y: MathF.Sin(x: pitch),
        z: (-MathF.Cos(x: yaw) * MathF.Cos(x: pitch))
    );
    private static WorldOperatorCamera Place(Vector3 pivot, float yaw, float pitch, float distance, float fieldOfView) {
        var wrapped = Wrap(radians: yaw);
        var clamped = ClampPitch(radians: pitch);
        var eye = (pivot - (Heading(
            pitch: clamped,
            yaw: wrapped
        ) * distance));

        return new WorldOperatorCamera(
            Distance: distance,
            FieldOfViewRadians: fieldOfView,
            PitchRadians: clamped,
            X: eye.X,
            Y: eye.Y,
            YawRadians: wrapped,
            Z: eye.Z
        );
    }

    /// <summary>Gets the eye position.</summary>
    /// <returns>The eye.</returns>
    public Vector3 Eye() => new(
        x: X,
        y: Y,
        z: Z
    );
    /// <summary>Gets the unit direction the camera faces.</summary>
    /// <returns>The forward vector.</returns>
    public Vector3 Forward() => Heading(
        pitch: PitchRadians,
        yaw: YawRadians
    );
    /// <summary>Gets the point the camera orbits: <see cref="Distance"/> metres ahead of the eye.</summary>
    /// <returns>The pivot.</returns>
    public Vector3 Pivot() => (Eye() + (Forward() * Distance));
    /// <summary>Returns the pose at an eye and a heading, in degrees.</summary>
    /// <param name="eye">The eye.</param>
    /// <param name="yawDegrees">The heading.</param>
    /// <param name="pitchDegrees">The elevation, clamped to <see cref="MaxPitchRadians"/>.</param>
    /// <param name="fieldOfViewRadians">The vertical field of view.</param>
    /// <returns>The pose.</returns>
    public static WorldOperatorCamera FromPose(Vector3 eye, float yawDegrees, float pitchDegrees, float fieldOfViewRadians) => new(
        Distance: DefaultDistance,
        FieldOfViewRadians: fieldOfViewRadians,
        PitchRadians: ClampPitch(radians: (pitchDegrees * DegreesToRadians)),
        X: eye.X,
        Y: eye.Y,
        YawRadians: Wrap(radians: (yawDegrees * DegreesToRadians)),
        Z: eye.Z
    );
    /// <summary>Returns the pose at <paramref name="eye"/> looking at <paramref name="target"/>, or
    /// <see langword="null"/> when they nearly coincide.</summary>
    /// <param name="eye">The eye.</param>
    /// <param name="target">The point to face; it becomes the swing pivot.</param>
    /// <param name="fieldOfViewRadians">The vertical field of view.</param>
    /// <returns>The pose, or <see langword="null"/> for a zero-length look.</returns>
    public static WorldOperatorCamera? FromLookAt(Vector3 eye, Vector3 target, float fieldOfViewRadians) {
        var ray = (target - eye);
        var distance = ray.Length();

        if (distance < MinDistance) {
            return null;
        }

        return new WorldOperatorCamera(
            Distance: distance,
            FieldOfViewRadians: fieldOfViewRadians,
            PitchRadians: ClampPitch(radians: MathF.Asin(x: Math.Clamp(
                value: (ray.Y / distance),
                min: -1f,
                max: 1f
            ))),
            X: eye.X,
            Y: eye.Y,
            YawRadians: MathF.Atan2(
                x: -ray.Z,
                y: -ray.X
            ),
            Z: eye.Z
        );
    }
    /// <summary>Returns the pose orbiting <paramref name="target"/> at a distance, heading and elevation (degrees): the
    /// eye sits behind the target along the heading.</summary>
    /// <param name="target">The pivot.</param>
    /// <param name="distance">The eye's distance from the pivot, at least <see cref="MinDistance"/>.</param>
    /// <param name="yawDegrees">The heading the camera faces.</param>
    /// <param name="pitchDegrees">The elevation, clamped to <see cref="MaxPitchRadians"/>.</param>
    /// <param name="fieldOfViewRadians">The vertical field of view.</param>
    /// <returns>The pose.</returns>
    public static WorldOperatorCamera FromOrbit(Vector3 target, float distance, float yawDegrees, float pitchDegrees, float fieldOfViewRadians) => Place(
        distance: Math.Max(
            val1: MinDistance,
            val2: distance
        ),
        fieldOfView: fieldOfViewRadians,
        pitch: (pitchDegrees * DegreesToRadians),
        pivot: target,
        yaw: (yawDegrees * DegreesToRadians)
    );
    /// <summary>Returns the pose moved by an offset in the camera's own axes, the pivot carried with it.</summary>
    /// <param name="right">Metres along the camera's right.</param>
    /// <param name="up">Metres along the camera's up.</param>
    /// <param name="forward">Metres along the camera's forward.</param>
    /// <returns>The moved pose.</returns>
    public WorldOperatorCamera Move(float right, float up, float forward) {
        var ahead = Forward();
        var side = new Vector3(
            x: -ahead.Z,
            y: 0f,
            z: ahead.X
        );

        // Straight up or down (a clamped 89 degree pitch is never exactly there) has no horizontal heading to take a
        // side from, so fall back to the yaw's own.
        side = ((side.LengthSquared() > 1e-12f)
            ? Vector3.Normalize(value: side)
            : new Vector3(
                x: -MathF.Cos(x: YawRadians),
                y: 0f,
                z: MathF.Sin(x: YawRadians)
            ));

        var lift = Vector3.Cross(
            vector1: side,
            vector2: ahead
        );
        var eye = (Eye() + (side * right) + (lift * up) + (ahead * forward));

        return (this with {
            X = eye.X,
            Y = eye.Y,
            Z = eye.Z,
        });
    }
    /// <summary>Returns the pose turned in place by a delta in degrees; the eye stays and the pivot swings.</summary>
    /// <param name="yawDegrees">The heading change; positive turns left.</param>
    /// <param name="pitchDegrees">The elevation change; positive looks up.</param>
    /// <returns>The turned pose.</returns>
    public WorldOperatorCamera Turn(float yawDegrees, float pitchDegrees) => (this with {
        PitchRadians = ClampPitch(radians: (PitchRadians + (pitchDegrees * DegreesToRadians))),
        YawRadians = Wrap(radians: (YawRadians + (yawDegrees * DegreesToRadians))),
    });
    /// <summary>Returns the pose swung about its pivot by a delta in degrees, the pivot fixed and the camera still
    /// facing it; the distance may change too.</summary>
    /// <param name="yawDegrees">The heading change, as <see cref="Turn"/> turns it; the eye circles the pivot the same way.</param>
    /// <param name="pitchDegrees">The elevation change; positive lowers the eye and tips the view up.</param>
    /// <param name="distance">The change in the eye's distance from the pivot.</param>
    /// <returns>The swung pose.</returns>
    public WorldOperatorCamera Swing(float yawDegrees, float pitchDegrees, float distance) => Place(
        distance: Math.Max(
            val1: MinDistance,
            val2: (Distance + distance)
        ),
        fieldOfView: FieldOfViewRadians,
        pitch: (PitchRadians + (pitchDegrees * DegreesToRadians)),
        pivot: Pivot(),
        yaw: (YawRadians + (yawDegrees * DegreesToRadians))
    );
    /// <summary>Returns the pose with another field of view.</summary>
    /// <param name="fieldOfViewRadians">The vertical field of view.</param>
    /// <returns>The pose.</returns>
    public WorldOperatorCamera WithFieldOfView(float fieldOfViewRadians) => (this with { FieldOfViewRadians = fieldOfViewRadians });
    /// <summary>Describes the pose the way every operator camera verb answers, in degrees and metres.</summary>
    /// <returns>The read-back text, such as <c>eye=(0,1.6,5) yaw=0 pitch=0 fov=60 distance=10 forward=(0,0,-1) pivot=(0,1.6,-5)</c>.</returns>
    public override string ToString() {
        var ahead = Forward();
        var pivot = Pivot();

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"eye=({Clean(value: X, digits: 3):0.###},{Clean(value: Y, digits: 3):0.###},{Clean(value: Z, digits: 3):0.###}) yaw={Clean(value: (YawRadians * RadiansToDegrees), digits: 3):0.###} pitch={Clean(value: (PitchRadians * RadiansToDegrees), digits: 3):0.###} fov={Clean(value: (FieldOfViewRadians * RadiansToDegrees), digits: 3):0.###} distance={Clean(value: Distance, digits: 3):0.###} forward=({Clean(value: ahead.X, digits: 4):0.####},{Clean(value: ahead.Y, digits: 4):0.####},{Clean(value: ahead.Z, digits: 4):0.####}) pivot=({Clean(value: pivot.X, digits: 3):0.###},{Clean(value: pivot.Y, digits: 3):0.###},{Clean(value: pivot.Z, digits: 3):0.###})"
        );
    }
    // Rounds to what the read-back prints, so a value that prints as zero is zero rather than "-0".
    private static float Clean(float value, int digits) => (MathF.Round(
        digits: digits,
        x: value
    ) + 0f);
}
