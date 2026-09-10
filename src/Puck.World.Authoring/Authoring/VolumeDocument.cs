using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>One local-density emission ramp stop.</summary>
public sealed record VolumeDensityStopDocument(float Density, string Color);
/// <summary>A bounded participating medium carried beside a creation's shapes. Its parent names a shape frame,
/// or null for the creation root. Ramp is evaluated against local density before ray integration.</summary>
public sealed record VolumeDocument(string Kind, DocumentVector3 Position, DocumentQuaternion Rotation,
    DocumentVector3 HalfExtent, IReadOnlyList<VolumeDensityStopDocument> Ramp, string? Parent = null,
    float? Axis = null, float? Width = null, float? Speed = null, uint? Seed = null, int? Steps = null,
    float? Intensity = null, float? Extinction = null, float? PulseAmplitude = null, float? PulseFrequency = null,
    int? IntensityLane = null) {
    /// <summary>The admitted density family.</summary>
    public const string FlowKind = "flow";
    /// <summary>The default advection speed in creation units per second.</summary>
    public const float DefaultSpeed = 1f;
    /// <summary>The default integration step count.</summary>
    public const int DefaultSteps = 32;
    /// <summary>The default width relative to the X half extent.</summary>
    public const float DefaultWidthShare = 1f;
    /// <summary>The default emission gain.</summary>
    public const float DefaultIntensity = 1f;
    /// <summary>The default absorption coefficient.</summary>
    public const float DefaultExtinction = 1f;
    /// <summary>Whether the family is supported.</summary>
    public bool IsKnownKind => Kind == FlowKind;
    /// <summary>Resolves the volume in the supplied parent frame and uniform scale.</summary>
    public SdfVolume ToVolume(int dynamicSlot, Vector3 origin, Quaternion rotation, float scale) => new(
        SdfVolumeKind.Flow, origin + Vector3.Transform(Position.Value * scale, rotation),
        Quaternion.Normalize(rotation * Rotation.Value), HalfExtent.Value * scale, dynamicSlot,
        (Axis ?? (2f * HalfExtent.Y)) * scale, (Width ?? (DefaultWidthShare * HalfExtent.X)) * scale,
        (Speed ?? DefaultSpeed) * scale, Seed ?? 0u, Steps ?? DefaultSteps,
        Ramp.Select(s => new SdfDensityStop(s.Density, HexColor.Parse(value: s.Color, fallback: Vector3.One))).ToArray(),
        Intensity ?? DefaultIntensity, Extinction ?? DefaultExtinction, PulseAmplitude ?? 0f, PulseFrequency ?? 0f, IntensityLane);
}
