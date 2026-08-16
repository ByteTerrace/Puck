using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.Physics.Tests.Geometry;

/// <summary>Which solid a body wears.</summary>
internal enum SpikeShapeKind {
    /// <summary>A sphere centred on the body's centre of mass.</summary>
    Sphere,
    /// <summary>A capsule whose segment is symmetric about the body's centre of mass.</summary>
    Capsule,
    /// <summary>A box centred on the body's centre of mass.</summary>
    Box,
}
/// <summary>One body's collision solid, in body axes.</summary>
/// <param name="Kind">Which solid this is.</param>
/// <param name="Radius">The sphere or capsule radius.</param>
/// <param name="SegmentHalf">Half the capsule's segment, in body axes; unused by the other kinds.</param>
/// <param name="HalfExtents">The box's half-extents, in body axes; unused by the other kinds.</param>
internal readonly record struct SpikeShape(SpikeShapeKind Kind, FixedQ4816 Radius, FixedVector3 SegmentHalf, FixedVector3 HalfExtents) {
    /// <summary>Creates a sphere.</summary>
    /// <param name="radius">The radius.</param>
    /// <returns>The shape.</returns>
    internal static SpikeShape Sphere(FixedQ4816 radius) =>
        new(Kind: SpikeShapeKind.Sphere, Radius: radius, SegmentHalf: FixedVector3.Zero, HalfExtents: FixedVector3.Zero);
    /// <summary>Creates a capsule.</summary>
    /// <param name="segmentHalf">Half the segment, in body axes.</param>
    /// <param name="radius">The radius.</param>
    /// <returns>The shape.</returns>
    internal static SpikeShape Capsule(FixedVector3 segmentHalf, FixedQ4816 radius) =>
        new(Kind: SpikeShapeKind.Capsule, Radius: radius, SegmentHalf: segmentHalf, HalfExtents: FixedVector3.Zero);
    /// <summary>Creates a box.</summary>
    /// <param name="halfExtents">The half-extents, in body axes.</param>
    /// <returns>The shape.</returns>
    internal static SpikeShape Box(FixedVector3 halfExtents) =>
        new(Kind: SpikeShapeKind.Box, Radius: FixedQ4816.Zero, SegmentHalf: FixedVector3.Zero, HalfExtents: halfExtents);
}
/// <summary>
/// A body's ABSOLUTE placement. It belongs to the fixture, never to the solver: candidate generation reads it, the
/// solver never does, and the fixture applies the solver's committed per-step displacement to it at the end of a step.
/// </summary>
internal sealed class BodyPose {
    /// <summary>The centre of mass in world coordinates.</summary>
    internal FixedVector3 Center { get; set; }
    /// <summary>The rotation taking body axes to world axes.</summary>
    internal FixedQuaternion Orientation { get; set; } = FixedQuaternion.Identity;
}
/// <summary>One static surface a body may contact.</summary>
internal interface ISpikeSurface {
    /// <summary>Gets the number of field samples this surface has consumed since the last reset.</summary>
    int SampleCount { get; }

    /// <summary>Clears the sample counter.</summary>
    void ResetSampleCount();
    /// <summary>Emits every candidate this surface believes the body may be touching.</summary>
    /// <param name="pose">The body's absolute placement.</param>
    /// <param name="shape">The body's solid.</param>
    /// <param name="activationBound">The separation at or below which a candidate is emitted.</param>
    /// <param name="output">The list candidates are appended to.</param>
    void Generate(BodyPose pose, SpikeShape shape, FixedQ4816 activationBound, List<FixedContactCandidate> output);
}
/// <summary>An unbounded half-space: the solid is everything with <c>dot(p, normal) &lt; offset</c>.</summary>
internal sealed class HalfSpaceSurface : ISpikeSurface {
    private readonly int m_sourceId;
    private readonly FixedVector3 m_normal;
    private readonly FixedQ4816 m_offset;

    /// <summary>Creates a half-space.</summary>
    /// <param name="sourceId">The surface's identity, which becomes one arm of every candidate's composite key.</param>
    /// <param name="normal">The outward unit normal.</param>
    /// <param name="offset">The plane's offset along <paramref name="normal"/>.</param>
    internal HalfSpaceSurface(int sourceId, FixedVector3 normal, FixedQ4816 offset) {
        m_sourceId = sourceId;
        m_normal = normal;
        m_offset = offset;
    }

    /// <inheritdoc/>
    public int SampleCount => 0;

    /// <inheritdoc/>
    public void ResetSampleCount() {
    }
    /// <inheritdoc/>
    public void Generate(BodyPose pose, SpikeShape shape, FixedQ4816 activationBound, List<FixedContactCandidate> output) {
        ArgumentNullException.ThrowIfNull(argument: pose);
        ArgumentNullException.ThrowIfNull(argument: output);

        var height = (FixedVector3.Dot(left: pose.Center, right: m_normal) - m_offset);

        switch (shape.Kind) {
            case SpikeShapeKind.Sphere: {
                    Emit(featureId: 0, offset: FixedVector3.Zero, separation: (height - shape.Radius), radius: shape.Radius, activationBound: activationBound, output: output);

                    break;
                }

            case SpikeShapeKind.Capsule: {
                    // A segment's support against a PLANE is always an endpoint, so the endpoint set is exact here; a
                    // curved or bounded surface is a different story, which is what the field surface below exists for.
                    var half = pose.Orientation.Rotate(vector: shape.SegmentHalf);

                    for (var index = 0; (index < 2); ++index) {
                        var end = ((index == 0) ? half : -half);

                        Emit(
                            featureId: index,
                            offset: end,
                            separation: ((height + FixedVector3.Dot(left: end, right: m_normal)) - shape.Radius),
                            radius: shape.Radius,
                            activationBound: activationBound,
                            output: output
                        );
                    }

                    break;
                }

            default: {
                    for (var index = 0; (index < 8); ++index) {
                        var corner = pose.Orientation.Rotate(vector: new FixedVector3(
                            X: (((index & 1) == 0) ? -shape.HalfExtents.X : shape.HalfExtents.X),
                            Y: (((index & 2) == 0) ? -shape.HalfExtents.Y : shape.HalfExtents.Y),
                            Z: (((index & 4) == 0) ? -shape.HalfExtents.Z : shape.HalfExtents.Z)
                        ));

                        Emit(
                            featureId: index,
                            offset: corner,
                            separation: (height + FixedVector3.Dot(left: corner, right: m_normal)),
                            radius: FixedQ4816.Zero,
                            activationBound: activationBound,
                            output: output
                        );
                    }

                    break;
                }
        }
    }

    private void Emit(int featureId, FixedVector3 offset, FixedQ4816 separation, FixedQ4816 radius, FixedQ4816 activationBound, List<FixedContactCandidate> output) {
        if (separation > activationBound) {
            return;
        }

        output.Add(item: new FixedContactCandidate(
            Anchor: (offset - (m_normal * radius)),
            FeatureId: featureId,
            Normal: m_normal,
            Separation: separation,
            SourceId: m_sourceId
        ));
    }
}
/// <summary>
/// A slab bounded by two parallel faces. Its witness is the NEAREST face, which is exactly the behaviour that makes a
/// deeply embedded body's normal untrustworthy: past the midplane the nearest face is the far one.
/// </summary>
internal sealed class SlabSurface : ISpikeSurface {
    private readonly int m_sourceId;
    private readonly FixedVector3 m_axis;
    private readonly FixedQ4816 m_lower;
    private readonly FixedQ4816 m_upper;

    /// <summary>Creates a slab.</summary>
    /// <param name="sourceId">The surface's identity.</param>
    /// <param name="axis">The unit axis the two faces are perpendicular to.</param>
    /// <param name="lower">The lower face's offset along <paramref name="axis"/>.</param>
    /// <param name="upper">The upper face's offset along <paramref name="axis"/>.</param>
    internal SlabSurface(int sourceId, FixedVector3 axis, FixedQ4816 lower, FixedQ4816 upper) {
        m_sourceId = sourceId;
        m_axis = axis;
        m_lower = lower;
        m_upper = upper;
    }

    /// <inheritdoc/>
    public int SampleCount => 0;

    /// <inheritdoc/>
    public void ResetSampleCount() {
    }
    /// <inheritdoc/>
    public void Generate(BodyPose pose, SpikeShape shape, FixedQ4816 activationBound, List<FixedContactCandidate> output) {
        ArgumentNullException.ThrowIfNull(argument: pose);
        ArgumentNullException.ThrowIfNull(argument: output);

        var height = FixedVector3.Dot(left: pose.Center, right: m_axis);
        var aboveGap = (height - m_upper);
        var belowGap = (m_lower - height);
        FixedVector3 normal;
        FixedQ4816 gap;

        if (aboveGap >= FixedQ4816.Zero) {
            normal = m_axis;
            gap = aboveGap;
        } else if (belowGap >= FixedQ4816.Zero) {
            normal = -m_axis;
            gap = belowGap;
        } else if (aboveGap >= belowGap) {
            // Inside, nearer the upper face.
            normal = m_axis;
            gap = aboveGap;
        } else {
            normal = -m_axis;
            gap = belowGap;
        }

        var separation = (gap - shape.Radius);

        if (separation > activationBound) {
            return;
        }

        output.Add(item: new FixedContactCandidate(
            SourceId: m_sourceId,
            FeatureId: 0,
            Anchor: -(normal * shape.Radius),
            Normal: normal,
            Separation: separation
        ));
    }
}
/// <summary>How a field surface looks for a capsule's witness.</summary>
internal enum CapsuleWitnessMode {
    /// <summary>Scan the whole segment, then refine the bracket around the minimum.</summary>
    SegmentScan,
    /// <summary>Sample the two endpoints only — the fixed recipe the design review refuted.</summary>
    EndpointsOnly,
}
/// <summary>
/// A surface described by a standalone <see cref="Puck.SignedDistance.SdfProgram"/>, read through the fixed-point evaluator. It
/// counts every field sample it takes, which is what makes the per-step sample budget a measured number rather than an
/// estimated one.
/// </summary>
internal sealed class FieldSurface : ISpikeSurface {
    private readonly int m_sourceId;
    private readonly IFieldEvaluator m_field;
    private readonly CapsuleWitnessMode m_mode;
    private readonly int m_scanSegments;
    private readonly int m_refinementSteps;

    private int m_sampleCount;

    /// <summary>Creates a field surface.</summary>
    /// <param name="sourceId">The surface's identity.</param>
    /// <param name="field">The evaluator wrapping the program.</param>
    /// <param name="mode">How a capsule's witness is found.</param>
    /// <param name="scanSegments">The number of equal segments the scan divides a capsule's axis into.</param>
    /// <param name="refinementSteps">The number of bracket refinements after the scan.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    internal FieldSurface(int sourceId, IFieldEvaluator field, CapsuleWitnessMode mode, int scanSegments, int refinementSteps) {
        ArgumentNullException.ThrowIfNull(argument: field);

        m_sourceId = sourceId;
        m_field = field;
        m_mode = mode;
        m_scanSegments = scanSegments;
        m_refinementSteps = refinementSteps;
    }

    /// <inheritdoc/>
    public int SampleCount => m_sampleCount;

    /// <summary>Gets the separation the two endpoint samples reported on the most recent generation.</summary>
    internal FixedQ4816 LastEndpointSeparation { get; private set; }
    /// <summary>Gets the separation the winning witness reported on the most recent generation.</summary>
    internal FixedQ4816 LastWitnessSeparation { get; private set; }

    /// <inheritdoc/>
    public void ResetSampleCount() {
        m_sampleCount = 0;
    }
    /// <inheritdoc/>
    public void Generate(BodyPose pose, SpikeShape shape, FixedQ4816 activationBound, List<FixedContactCandidate> output) {
        ArgumentNullException.ThrowIfNull(argument: pose);
        ArgumentNullException.ThrowIfNull(argument: output);

        if (shape.Kind == SpikeShapeKind.Sphere) {
            EmitAt(pose: pose, witness: pose.Center, distance: Distance(point: pose.Center), radius: shape.Radius, featureId: 0, activationBound: activationBound, output: output);

            return;
        }

        var half = pose.Orientation.Rotate(vector: shape.SegmentHalf);
        var start = (pose.Center + half);
        var end = (pose.Center - half);
        var startDistance = Distance(point: start);
        var endDistance = Distance(point: end);

        LastEndpointSeparation = (FixedQ4816.Min(x: startDistance, y: endDistance) - shape.Radius);

        if (m_mode == CapsuleWitnessMode.EndpointsOnly) {
            EmitAt(pose: pose, witness: start, distance: startDistance, radius: shape.Radius, featureId: 0, activationBound: activationBound, output: output);
            EmitAt(pose: pose, witness: end, distance: endDistance, radius: shape.Radius, featureId: 1, activationBound: activationBound, output: output);
            LastWitnessSeparation = LastEndpointSeparation;

            return;
        }

        var (witness, distance) = FindWitness(end: end, endDistance: endDistance, start: start, startDistance: startDistance);

        LastWitnessSeparation = (distance - shape.Radius);
        EmitAt(pose: pose, witness: witness, distance: distance, radius: shape.Radius, featureId: 2, activationBound: activationBound, output: output);
    }

    // A uniform scan of the segment followed by a bracket refinement. Both halves visit a FIXED, declared number of
    // parameters in ascending order and break every tie toward the lower one, so the witness is a deterministic
    // function of the pose alone.
    private (FixedVector3 Witness, FixedQ4816 Distance) FindWitness(FixedVector3 start, FixedVector3 end, FixedQ4816 startDistance, FixedQ4816 endDistance) {
        var stepCount = FixedQ4816.FromInteger(value: m_scanSegments);
        var bestIndex = 0;
        var bestDistance = startDistance;

        for (var index = 1; (index <= m_scanSegments); ++index) {
            var distance = ((index == m_scanSegments)
                ? endDistance
                : Distance(point: FixedVector3.Lerp(from: start, to: end, amount: (FixedQ4816.FromInteger(value: index) / stepCount))));

            if (distance < bestDistance) {
                bestIndex = index;
                bestDistance = distance;
            }
        }

        var low = (FixedQ4816.FromInteger(value: Math.Max(val1: (bestIndex - 1), val2: 0)) / stepCount);
        var high = (FixedQ4816.FromInteger(value: Math.Min(val1: (bestIndex + 1), val2: m_scanSegments)) / stepCount);
        var mid = (FixedQ4816.FromInteger(value: bestIndex) / stepCount);

        for (var refinement = 0; (refinement < m_refinementSteps); ++refinement) {
            var lowMid = ((low + mid) * FixedQ4816.FromDouble(value: 0.5d));
            var highMid = ((mid + high) * FixedQ4816.FromDouble(value: 0.5d));
            var lowMidDistance = Distance(point: FixedVector3.Lerp(amount: lowMid, from: start, to: end));
            var highMidDistance = Distance(point: FixedVector3.Lerp(amount: highMid, from: start, to: end));

            if (lowMidDistance < bestDistance) {
                high = mid;
                mid = lowMid;
                bestDistance = lowMidDistance;
            } else if (highMidDistance < bestDistance) {
                low = mid;
                mid = highMid;
                bestDistance = highMidDistance;
            } else {
                low = lowMid;
                high = highMid;
            }
        }

        return (FixedVector3.Lerp(amount: mid, from: start, to: end), bestDistance);
    }
    private void EmitAt(BodyPose pose, FixedVector3 witness, FixedQ4816 distance, FixedQ4816 radius, int featureId, FixedQ4816 activationBound, List<FixedContactCandidate> output) {
        var separation = (distance - radius);

        if (separation > activationBound) {
            return;
        }

        m_sampleCount += 6;

        if (!m_field.TryFieldGradient(position: FixedPosition.FromLocal(local: witness), gradient: out var normal)) {
            return;
        }

        output.Add(item: new FixedContactCandidate(
            SourceId: m_sourceId,
            FeatureId: featureId,
            Anchor: ((witness - pose.Center) - (normal * radius)),
            Normal: normal,
            Separation: separation
        ));
    }
    private FixedQ4816 Distance(FixedVector3 point) {
        ++m_sampleCount;

        return (m_field.TryDistance(position: FixedPosition.FromLocal(local: point), distance: out var distance, material: out _)
            ? distance
            : FixedQ4816.MaxValue);
    }
}
