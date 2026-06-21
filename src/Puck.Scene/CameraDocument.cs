using System.Text.Json.Serialization;
using Puck.Cameras;

namespace Puck.Scene;

/// <summary>
/// A viewport's camera, authored as data. The <c>$type</c> string is the JSON discriminator; field-of-view is
/// authored in DEGREES (converted to the engine's radians at build time, exactly as the demo's hand-authored cameras
/// do). Adding a camera kind is a new derived record carrying its own <see cref="Build"/> and <see cref="Validate"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(OrbitCameraDocument), typeDiscriminator: "orbit")]
[JsonDerivedType(typeof(PerspectiveCameraDocument), typeDiscriminator: "perspective")]
public abstract record CameraDocument {
    // Degrees -> radians using the EXACT expression the demo uses, so a JSON camera reproduces a hand-authored one
    // bit-for-bit (float rounding included).
    private protected static float ToRadians(float degrees) {
        return (degrees * (MathF.PI / 180f));
    }

    internal abstract ICamera Build();
    internal abstract void Validate(string path, ValidationErrors errors);
}

/// <summary>An orbiting camera that circles a target at a fixed height and radius (maps to <see cref="OrbitCamera"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrbitCameraDocument : CameraDocument {
    /// <summary>The orbit speed in radians per second (may be negative to orbit the other way).</summary>
    public float AngularSpeed { get; init; }
    /// <summary>The initial orbit angle in radians.</summary>
    public float Azimuth { get; init; }
    /// <summary>The vertical field of view in DEGREES.</summary>
    public float FieldOfView { get; init; }
    /// <summary>The camera height above the target plane.</summary>
    public float Height { get; init; }
    /// <summary>The orbit radius.</summary>
    public float Radius { get; init; }
    /// <summary>The point the camera orbits and looks at, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Target { get; init; } = [];

    internal override ICamera Build() {
        return new OrbitCamera {
            AngularSpeedRadiansPerSecond = AngularSpeed,
            AzimuthRadians = Azimuth,
            FieldOfViewRadians = ToRadians(degrees: FieldOfView),
            Height = Height,
            Radius = Radius,
            Target = JsonVector.ToVector3(components: Target),
        };
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireFinite(path: $"{path}.angularSpeed", name: "angularSpeed", value: AngularSpeed);
        errors.RequireFinite(path: $"{path}.azimuth", name: "azimuth", value: Azimuth);
        errors.RequireFinite(path: $"{path}.height", name: "height", value: Height);
        errors.RequireRange(path: $"{path}.fieldOfView", name: "fieldOfView", range: new FloatRange(Maximum: 179f, Minimum: 1f), value: FieldOfView);
        errors.RequireRange(path: $"{path}.radius", name: "radius", range: new FloatRange(Maximum: float.MaxValue, Minimum: 0f), value: Radius);
        errors.RequireVector(path: $"{path}.target", components: Target, length: 3);
    }
}

/// <summary>A fixed look-at camera (maps to <see cref="PerspectiveCamera"/>).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PerspectiveCameraDocument : CameraDocument {
    /// <summary>The vertical field of view in DEGREES.</summary>
    public float FieldOfView { get; init; }
    /// <summary>The eye position, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Position { get; init; } = [];
    /// <summary>The point the camera looks at, as a 3-element <c>[x, y, z]</c> array.</summary>
    public IReadOnlyList<float> Target { get; init; } = [];

    internal override ICamera Build() {
        return new PerspectiveCamera {
            FieldOfViewRadians = ToRadians(degrees: FieldOfView),
            Position = JsonVector.ToVector3(components: Position),
            Target = JsonVector.ToVector3(components: Target),
        };
    }
    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireRange(path: $"{path}.fieldOfView", name: "fieldOfView", range: new FloatRange(Maximum: 179f, Minimum: 1f), value: FieldOfView);
        errors.RequireVector(path: $"{path}.position", components: Position, length: 3);
        errors.RequireVector(path: $"{path}.target", components: Target, length: 3);
    }
}
