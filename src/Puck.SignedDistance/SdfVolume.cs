using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The bounded participating medium's density family.</summary>
public enum SdfVolumeKind {
    /// <summary>An advected scalar density field inside a tapered column.</summary>
    Flow,
    /// <summary>A bounded, advected cloud with a three-dimensional noise density.</summary>
    Cloud,
}
/// <summary>One ascending local-density threshold and its linear emission color.</summary>
public readonly record struct SdfDensityStop(float Density, Vector3 Color);
/// <summary>A bounded participating medium, separate from the distance-field tape. Position and Rotation are
/// relative to DynamicSlot, or world space for -1. A flow column runs from the box's +Y face toward -Y;
/// a cloud fills an ellipsoidal envelope inside the box, with Width setting its noise-cell size.
/// Ramp contains one to four ascending density stops, evaluated at each sample before integration.
/// PulseAmplitude is in [0, 1]; PulseFrequency is cycles per second. IntensityLane selects [0, 3], or null
/// for constant gain. An authored lane on a static volume reads zero. Seed preserves all 32 bits.
/// Coverage is in [0, 1]; Softness is the cloud density transition width in (0, 1].</summary>
public readonly record struct SdfVolume(SdfVolumeKind Kind, Vector3 Position, Quaternion Rotation, Vector3 HalfExtent,
    int DynamicSlot, float Axis, float Width, float Speed, uint Seed, int Steps, IReadOnlyList<SdfDensityStop> Ramp,
    float Intensity, float Extinction, float PulseAmplitude = 0f, float PulseFrequency = 0f, int? IntensityLane = null,
    float Coverage = 0.55f, float Softness = 0.18f) {
    /// <summary>The smallest admitted integration step count.</summary>
    public const int MinSteps = 8;
    /// <summary>The largest admitted integration step count.</summary>
    public const int MaxSteps = 64;
    /// <summary>The packed float4 stride, paired with shade-volumes.hlsli.</summary>
    public const int VectorsPerEntry = 11;
    /// <summary>Refuses invalid engine inputs before they reach the GPU.</summary>
    public void Validate(int dynamicTransformCount) {
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        static bool NonNegative(float v) => float.IsFinite(v) && v >= 0f;
        if ((Kind != SdfVolumeKind.Flow && Kind != SdfVolumeKind.Cloud) || !Finite(Position) || !Finite(HalfExtent) ||
            HalfExtent.X <= 0f || HalfExtent.Y <= 0f || HalfExtent.Z <= 0f ||
            !float.IsFinite(Rotation.LengthSquared()) || Rotation.LengthSquared() < 1e-12f ||
            DynamicSlot < -1 || DynamicSlot >= dynamicTransformCount ||
            !NonNegative(Axis) || Axis == 0f || !NonNegative(Width) || Width == 0f || !float.IsFinite(Speed) ||
            Steps < MinSteps || Steps > MaxSteps || !NonNegative(Intensity) || !NonNegative(Extinction) ||
            !NonNegative(PulseAmplitude) || PulseAmplitude > 1f || !NonNegative(PulseFrequency) ||
            (IntensityLane is { } lane && (uint)lane > 3u) || Ramp is not { Count: >= 1 and <= 4 } ||
            !NonNegative(Coverage) || Coverage > 1f || !NonNegative(Softness) || Softness == 0f || Softness > 1f) {
            throw new ArgumentOutOfRangeException(nameof(SdfVolume), "Invalid bounded volume controls, frame, lane, or ramp.");
        }
        var previous = -1f;
        foreach (var stop in Ramp) {
            if (!NonNegative(stop.Density) || stop.Density > 1f || stop.Density <= previous ||
                !Finite(stop.Color) || stop.Color.X < 0f || stop.Color.Y < 0f || stop.Color.Z < 0f) {
                throw new ArgumentOutOfRangeException(nameof(Ramp), "Density stops must increase in [0, 1] with finite non-negative colors.");
            }
            previous = stop.Density;
        }
    }
}
