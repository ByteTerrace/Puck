using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.SignedDistance;

/// <summary>The solid an <see cref="SdfOccluder"/> stands for.</summary>
public enum SdfOccluderShape : uint {
    /// <summary>A sphere about <see cref="SdfOccluder.Center"/> of <see cref="SdfOccluder.Radius"/>.</summary>
    Sphere = 0,
    /// <summary>A capsule: the points within <see cref="SdfOccluder.Radius"/> of the segment from
    /// <see cref="SdfOccluder.Center"/> less <see cref="SdfOccluder.AxisX"/> to it plus <see cref="SdfOccluder.AxisX"/>.</summary>
    Capsule = 1,
    /// <summary>An oriented box about <see cref="SdfOccluder.Center"/> whose half axes are <see cref="SdfOccluder.AxisX"/>,
    /// <see cref="SdfOccluder.AxisY"/> and <see cref="SdfOccluder.AxisZ"/>, mutually perpendicular.</summary>
    Box = 2,
}

/// <summary>Which solid a creation's occluder is fitted as (<see cref="SdfOccluder.Fit"/>).</summary>
[JsonConverter(typeof(StrictEnumConverter<SdfOccluderFit>))]
public enum SdfOccluderFit {
    /// <summary>The solid the box's proportions suggest: a sphere for a box near a cube, a capsule along the long axis
    /// for a box whose two short sides are alike, and the box itself otherwise.</summary>
    Auto = 0,
    /// <summary>A sphere.</summary>
    Sphere = 1,
    /// <summary>A capsule along the box's longest axis.</summary>
    Capsule = 2,
    /// <summary>The box itself.</summary>
    Box = 3,
}

/// <summary>
/// One analytic occluder: a sphere, capsule or box standing in for a creation, which darkens the ambient and indirect
/// light of the surfaces about it by the share of their hemisphere it covers (Quilez, "sphere ambient occlusion" and
/// "box occlusion", with Lagarde and de Rousiers' horizon clip for a sphere the horizon cuts) and casts a soft shadow
/// toward every ranged light, screen and emitter face (Quilez, "sphere soft shadow" and "capsule soft shadow"); never
/// toward a directional light, whose cascades shadow it. It is one record of the occluders table the kernels read
/// (<c>sdfOccluders</c>, a World-group structured buffer of <see cref="SdfOccluders.MaxOccluders"/> records): its public
/// fields are the record's layout, whose HLSL declaration <c>puck shaders generate</c> writes from this type. Every
/// value is in world space and presentation float; nothing here reaches the simulation.
/// </summary>
/// <remarks>An occluder's ambient term adds nothing at a point <see cref="Reach"/> or farther from its center, eased to
/// exactly zero there by a light's window (<c>sdfLightWindow</c>), and its shadow toward a light reaches as far as the
/// light does, zero only where the segment from a point to the light passes its <see cref="BoundingRadius"/> by the
/// penumbra or more. A view and each views tile leave out an occluder neither term can reach what they shade with
/// (<see cref="SdfLightSelection.SelectOccluders"/>, <c>sdfOccluderReachesWalk</c>).</remarks>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public record struct SdfOccluder {
    /// <summary>The solid's center, in world space.</summary>
    [FieldOffset(0)] public Vector3 Center;
    /// <summary>The share, in [0, 1], of the ambient and indirect light its covered hemisphere takes away.</summary>
    [FieldOffset(12)] public float Strength;
    /// <summary>A capsule's half segment; a box's first half axis; zero for a sphere.</summary>
    [FieldOffset(16)] public Vector3 AxisX;
    /// <summary>What the solid is.</summary>
    [FieldOffset(28)] public SdfOccluderShape Shape;
    /// <summary>A box's second half axis; zero otherwise.</summary>
    [FieldOffset(32)] public Vector3 AxisY;
    /// <summary>A sphere's or a capsule's radius; zero for a box.</summary>
    [FieldOffset(44)] public float Radius;
    /// <summary>A box's third half axis; zero otherwise.</summary>
    [FieldOffset(48)] public Vector3 AxisZ;
    /// <summary>The distance from <see cref="Center"/> past which the occluder takes nothing from the ambient and indirect
    /// light: its bounding radius plus the reach its author gave it. Its shadow is not bounded by it.</summary>
    [FieldOffset(60)] public float Reach;
    /// <summary>The share, in [0, 1], of a ranged light, screen or emitter face its shadow takes away where it covers the
    /// light whole.</summary>
    [FieldOffset(64)] public float Shadow;
    /// <summary>The penumbra's half slope toward a light of no size (a point or spot light): its shadow's edge widens by
    /// this much for each unit from the occluder to the shaded surface. A light with a size (a screen, an emitter face, a
    /// tube) widens it by its own half slope where that is the wider. Zero casts a point light's hard edge.</summary>
    [FieldOffset(68)] public float Softness;
    /// <summary>The first dynamic-transform slot of the body the occluder stands for. A surface whose frame slot lies in
    /// the <see cref="OwnSlots"/> slots from it is that body's own, which its own occluder neither darkens nor
    /// shadows.</summary>
    [FieldOffset(72)] public int OwnSlot;
    /// <summary>How many slots from <see cref="OwnSlot"/> the body's own surfaces ride; zero for an occluder that stands
    /// for no moving body, which darkens every surface about it.</summary>
    [FieldOffset(76)] public uint OwnSlots;

    /// <summary>The proportion of a box's longest to its shortest half extent under which <see cref="SdfOccluderFit.Auto"/>
    /// fits a sphere.</summary>
    public const float SphereProportion = 1.3f;
    /// <summary>The proportion of a box's middle to its shortest half extent under which <see cref="SdfOccluderFit.Auto"/>
    /// fits a capsule along the longest axis.</summary>
    public const float CapsuleProportion = 2f;

    /// <summary>Gets the radius of the sphere about <see cref="Center"/> that holds the whole solid.</summary>
    public readonly float BoundingRadius => (Shape switch {
        SdfOccluderShape.Capsule => (AxisX.Length() + Radius),
        SdfOccluderShape.Box => MathF.Sqrt(x: ((AxisX.LengthSquared() + AxisY.LengthSquared()) + AxisZ.LengthSquared())),
        _ => Radius,
    });

    /// <summary>Returns the occluder that stands for an axis-aligned box in a frame of its own (a creation's rest bound in
    /// the creation's frame): a sphere of the box's half extents' geometric mean, a capsule along its longest axis whose
    /// radius is the other two's geometric mean, or the box itself. The sphere and the capsule lie about as far inside
    /// the box as outside it, so they cover about what it covers.</summary>
    /// <param name="low">The box's low corner, in the frame.</param>
    /// <param name="high">The box's high corner, in the frame.</param>
    /// <param name="fit">Which solid to fit.</param>
    /// <returns>The occluder in the box's frame, its strengths, reach and softness zero; <see langword="null"/> for a box
    /// with no volume.</returns>
    public static SdfOccluder? Fit(Vector3 low, Vector3 high, SdfOccluderFit fit) {
        var half = (0.5f * (high - low));

        if (!(half.X > 0f) || !(half.Y > 0f) || !(half.Z > 0f) || !float.IsFinite(f: ((half.X + half.Y) + half.Z))) {
            return null;
        }

        var center = (0.5f * (high + low));
        // The axes from the longest half extent to the shortest.
        Span<int> order = [0, 1, 2];

        order.Sort(comparison: (left, right) => half[right].CompareTo(value: half[left]));

        var longest = half[order[0]];
        var middle = half[order[1]];
        var shortest = half[order[2]];
        var shape = (fit switch {
            SdfOccluderFit.Sphere => SdfOccluderShape.Sphere,
            SdfOccluderFit.Capsule => SdfOccluderShape.Capsule,
            SdfOccluderFit.Box => SdfOccluderShape.Box,
            _ => ((longest < (SphereProportion * shortest))
                ? SdfOccluderShape.Sphere
                : ((middle < (CapsuleProportion * shortest)) ? SdfOccluderShape.Capsule : SdfOccluderShape.Box)),
        });

        return shape switch {
            SdfOccluderShape.Sphere => new SdfOccluder { Center = center, Radius = MathF.Cbrt(x: ((half.X * half.Y) * half.Z)), Shape = shape },
            SdfOccluderShape.Capsule => CapsuleAlong(axis: order[0], center: center, longest: longest, radius: MathF.Sqrt(x: (middle * shortest))),
            _ => new SdfOccluder { AxisX = new Vector3(x: half.X, y: 0f, z: 0f), AxisY = new Vector3(x: 0f, y: half.Y, z: 0f), AxisZ = new Vector3(x: 0f, y: 0f, z: half.Z), Center = center, Shape = shape },
        };

        static SdfOccluder CapsuleAlong(int axis, Vector3 center, float longest, float radius) {
            var direction = Vector3.Zero;

            direction[axis] = 1f;

            return new SdfOccluder {
                AxisX = (direction * MathF.Max(x: (longest - radius), y: 0f)),
                Center = center,
                Radius = radius,
                Shape = SdfOccluderShape.Capsule,
            };
        }
    }
    /// <summary>Returns this occluder carried into another frame: scaled, turned and moved, its strengths, reach and
    /// softness kept.</summary>
    /// <param name="position">Where the frame's origin stands.</param>
    /// <param name="orientation">How the frame is turned.</param>
    /// <param name="scale">The frame's uniform scale.</param>
    /// <returns>The occluder in the outer frame.</returns>
    public readonly SdfOccluder Placed(Vector3 position, Quaternion orientation, float scale) => (this with {
        AxisX = Vector3.Transform(value: (AxisX * scale), rotation: orientation),
        AxisY = Vector3.Transform(value: (AxisY * scale), rotation: orientation),
        AxisZ = Vector3.Transform(value: (AxisZ * scale), rotation: orientation),
        Center = (position + Vector3.Transform(value: (Center * scale), rotation: orientation)),
        Radius = (Radius * scale),
        Reach = (Reach * scale),
    });
}

/// <summary>
/// A frame's occluders table: up to <see cref="MaxOccluders"/> <see cref="SdfOccluder"/> records, which the tables pack
/// into the World-group table the views and glass passes read (<see cref="Pack"/>). Each view shades only the occluders
/// that can reach what it shows (<see cref="SdfLightSelection.SelectOccluders"/>),
/// whose mask its pass block carries one bit a record.
/// </summary>
public sealed class SdfOccluders {
    /// <summary>The most occluders a frame carries: the records of the occluders table, one bit each of a view's
    /// mask.</summary>
    public const int MaxOccluders = 128;

    private readonly SdfOccluder[] m_occluders = new SdfOccluder[MaxOccluders];
    // Who cast each record, for a read-back; never packed.
    private readonly string?[] m_sources = new string?[MaxOccluders];

    private int m_count;

    /// <summary>Gets an empty table, which no producer writes.</summary>
    public static SdfOccluders Empty { get; } = new();

    /// <summary>Gets the number of occluders, at most <see cref="MaxOccluders"/>.</summary>
    public int Count => m_count;
    /// <summary>Gets the occluders, <see cref="Count"/> of them.</summary>
    public ReadOnlySpan<SdfOccluder> Records => m_occluders.AsSpan(start: 0, length: m_count);

    /// <summary>Gets one occluder.</summary>
    /// <param name="index">The occluder's index, below <see cref="Count"/>.</param>
    /// <returns>The occluder.</returns>
    public SdfOccluder this[int index] => Records[index];
    /// <summary>Gets who cast one occluder, as its producer named it: presentation metadata a read-back prints, never
    /// part of the packed record.</summary>
    /// <param name="index">The occluder's index, below <see cref="Count"/>.</param>
    /// <returns>The source's name, or <see langword="null"/> where the producer named none.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not below
    /// <see cref="Count"/>.</exception>
    public string? SourceOf(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: m_count);

        return m_sources[index];
    }

    /// <summary>Empties the table.</summary>
    public void Clear() => m_count = 0;
    /// <summary>Appends an occluder whose strengths, reach and softness are each finite and nonnegative, its strengths
    /// clamped to one.</summary>
    /// <param name="occluder">The occluder.</param>
    /// <param name="source">Who cast it (<see cref="SourceOf"/>), or <see langword="null"/>.</param>
    /// <returns><see langword="false"/> when the table is full or the occluder can add nothing (no strength, or no
    /// reach), and was not appended.</returns>
    public bool TryAdd(in SdfOccluder occluder, string? source = null) {
        if ((m_count >= MaxOccluders) || !float.IsFinite(f: occluder.Reach) || !(occluder.Reach > 0f) || !float.IsFinite(f: occluder.Softness)) {
            return false;
        }

        var strength = Math.Clamp(value: (float.IsFinite(f: occluder.Strength) ? occluder.Strength : 0f), min: 0f, max: 1f);
        var shadow = Math.Clamp(value: (float.IsFinite(f: occluder.Shadow) ? occluder.Shadow : 0f), min: 0f, max: 1f);

        if (!(strength > 0f) && !(shadow > 0f)) {
            return false;
        }

        m_sources[m_count] = source;
        m_occluders[m_count++] = (occluder with { Shadow = shadow, Softness = MathF.Max(x: occluder.Softness, y: 0f), Strength = strength });

        return true;
    }
    /// <summary>Copies every occluder from another table.</summary>
    /// <param name="source">The table to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void CopyFrom(SdfOccluders source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        source.Records.CopyTo(destination: m_occluders);
        source.m_sources.AsSpan(start: 0, length: source.m_count).CopyTo(destination: m_sources);
        m_count = source.m_count;
    }
    /// <summary>Packs the occluders into the table the kernels read, every record past <see cref="Count"/> zero.</summary>
    /// <param name="records">The table, at least <see cref="MaxOccluders"/> records.</param>
    /// <exception cref="ArgumentException"><paramref name="records"/> holds fewer than <see cref="MaxOccluders"/>
    /// records.</exception>
    public void Pack(Span<SdfOccluder> records) {
        if (records.Length < MaxOccluders) {
            throw new ArgumentException(message: $"The occluders table holds {MaxOccluders} records; the span holds {records.Length}.", paramName: nameof(records));
        }

        Records.CopyTo(destination: records);
        records[m_count..MaxOccluders].Clear();
    }
}
