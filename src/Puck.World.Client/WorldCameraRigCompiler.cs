using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World;

/// <summary>Lets a compiled camera rig be retargeted onto the current live document every frame — an authored
/// <see cref="WorldCameraProgramOp.Fov"/>/<see cref="WorldCameraProgramOp.Blend"/> op's state binding must read the
/// LIVE document, never the one a cached rig happened to compile against; every camera-rig caller that reuses a
/// compiled rig across frames (a seat's chase rig — see <c>WorldSeatCameraResolver.ResolveChase</c>) calls this once
/// per frame before <see cref="ISdfCameraRig.Resolve"/>. A caller that recompiles fresh every frame (a named camera,
/// a possessed camera body) has no stale state to refresh and may skip it.</summary>
public interface IWorldCameraProgramRig : ISdfCameraRig {
    /// <summary>Repoints this rig's state-binding reads at the current live document.</summary>
    /// <param name="definition">The current document.</param>
    void Retarget(WorldDefinition definition);
}
/// <summary>Compiles an authored camera program into one presentation rig — the evaluator for the ordered op-list
/// vocabulary <c>bodyMotionPrograms</c> established for sim-side movement, promoted to cameras
/// (<see cref="WorldCameraProgram"/>). Replaces the old closed <c>WorldCameraMotion</c>/<c>WorldCameraAim</c> union's
/// per-kind resolvers with one op-walking evaluator.</summary>
public static class WorldCameraRigCompiler {
    private const int MaxBlendDepth = 8;

    /// <summary>Gets the motion's authored eye or pivot position for editor/narration purposes — the orbit's resolved
    /// offset from its pivot, the offset op's raw value, or the origin for a program authoring neither.</summary>
    public static Vector3 AuthoredPosition(WorldCameraProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        if (program.OrbitOp is { } orbit) {
            return (orbit.PivotOffset.Value + OrbitRig.Offset(
                yaw: orbit.Yaw,
                pitch: orbit.Pitch,
                distance: orbit.Distance
            ));
        }

        return (program.OffsetOp?.Value.Value ?? Vector3.Zero);
    }
    /// <summary>Compiles an authored camera program.</summary>
    /// <param name="program">The authored op list.</param>
    /// <param name="definition">The document to resolve this program's state bindings against — refreshed every
    /// frame via <see cref="IWorldCameraProgramRig.Retarget"/> for a caller that caches the returned rig.</param>
    /// <param name="referenceOffset">An additional reference-local offset, such as an entity-leaf rest offset.</param>
    /// <param name="spread">The resolved group spread.</param>
    /// <returns>A fresh presentation rig.</returns>
    public static IWorldCameraProgramRig Compile(WorldCameraProgram program, WorldDefinition definition, Vector3 referenceOffset = default, float spread = 0f) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new ComposedWorldCameraProgramRig(
            definition: definition,
            program: program,
            referenceOffset: referenceOffset,
            spread: spread
        );
    }

    private readonly record struct Subject(Vector3 Position, Quaternion Orientation);

    private sealed class ComposedWorldCameraProgramRig(WorldCameraProgram program, WorldDefinition definition, Vector3 referenceOffset, float spread) : IWorldCameraProgramRig {
        private WorldDefinition m_definition = definition;

        public void Retarget(WorldDefinition definition) {
            ArgumentNullException.ThrowIfNull(argument: definition);

            m_definition = definition;
        }

        private Subject ResolveSubject(WorldCameraSubject? subject, in SdfAnchor reference) => subject switch {
            null or WorldCameraSubject.Reference => new Subject(
                Orientation: reference.Orientation,
                Position: (reference.Position + Vector3.Transform(
                    value: referenceOffset,
                    rotation: reference.Orientation
                ))
            ),
            WorldCameraSubject.Placement placement => new Subject(
                Orientation: Quaternion.Identity,
                Position: WorldAnchorGeometry.StaticPlacementPosition(
                    definition: m_definition,
                    placementId: placement.PlacementId,
                    shapeId: placement.ShapeId
                )
            ),
            WorldCameraSubject.WorldPoint worldPoint => new Subject(
                Orientation: Quaternion.Identity,
                Position: worldPoint.Point.Value
            ),
            var other => throw new ArgumentOutOfRangeException(
                paramName: nameof(subject),
                actualValue: other,
                message: $"unknown camera subject kind '{(other?.GetType().Name ?? "<null>")}'."
            ),
        };
        private static Vector3 ResolveOffset(in Subject subject, DocumentVector3 value, bool worldAxes, float spreadPullback) {
            var scaled = (value.Value * (1f + (spreadPullback * MathF.Max(
                x: spread,
                y: 0f
            ))));

            return (subject.Position + (worldAxes
                ? scaled
                : Vector3.Transform(
                    rotation: subject.Orientation,
                    value: scaled
                )
            ));
        }

        public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, in SdfCameraClock clock) => Evaluate(
            anchor: in anchor,
            clock: in clock,
            depth: 0,
            program: program
        );
        private (Vector3 Eye, Vector3 Target, float FovRadians) Evaluate(WorldCameraProgram program, in SdfAnchor anchor, in SdfCameraClock clock, int depth) {
            var subject = ResolveSubject(
                reference: in anchor,
                subject: null
            );
            var eye = subject.Position;
            var target = eye;
            var haveTarget = false;
            var fov = OrbitRig.DefaultFieldOfViewRadians;
            var pitchMin = (-MathF.PI / 2f);
            var pitchMax = (MathF.PI / 2f);
            var operations = program.Operations;

            for (var index = 0; ((operations is not null) && (index < operations.Count)); index++) {
                switch (operations[index]) {
                    case WorldCameraProgramOp.Anchor anchorOp:
                        subject = ResolveSubject(
                            reference: in anchor,
                            subject: anchorOp.Subject
                        );
                        eye = subject.Position;

                        break;
                    case WorldCameraProgramOp.Offset offset:
                        eye = ResolveOffset(
                            spreadPullback: offset.SpreadPullback,
                            subject: in subject,
                            value: offset.Value,
                            worldAxes: offset.WorldAxes
                        );

                        break;
                    case WorldCameraProgramOp.Orbit orbit:
                        var clampedPitch = Math.Clamp(
                            value: orbit.Pitch,
                            min: pitchMin,
                            max: pitchMax
                        );
                        var pivot = (subject.Position + orbit.PivotOffset.Value);

                        eye = (pivot + OrbitRig.Offset(
                            yaw: orbit.Yaw,
                            pitch: clampedPitch,
                            distance: orbit.Distance
                        ));

                        break;
                    case WorldCameraProgramOp.LookAt lookAt:
                        target = ((lookAt.Subject is { } lookSubject)
                            ? ResolveOffset(
                                spreadPullback: 0f,
                                subject: ResolveSubject(
                                    reference: in anchor,
                                    subject: lookSubject
                                ),
                                value: lookAt.Offset,
                                worldAxes: lookAt.WorldAxes
                            )
                            : (eye + (Vector3.Transform(
                                value: -Vector3.UnitZ,
                                rotation: subject.Orientation
                            ) * MathF.Max(
                                x: lookAt.FocusDistance,
                                y: 0.01f
                            ))));
                        haveTarget = true;

                        break;
                    case WorldCameraProgramOp.Smooth:
                        // Read externally (WorldCameraProgram.SmoothOp) — never affects the resolved pose.
                        break;
                    case WorldCameraProgramOp.ClampPitch clampPitch:
                        pitchMin = clampPitch.MinPitch;
                        pitchMax = clampPitch.MaxPitch;

                        break;
                    case WorldCameraProgramOp.Fov fovOp:
                        fov = fovOp.FieldOfViewRadians.Resolve(
                            definition: m_definition,
                            fallback: fov,
                            tick: clock.AuthoritativeTick
                        );

                        break;
                    case WorldCameraProgramOp.Blend blend:
                        if (depth >= MaxBlendDepth) {
                            break;
                        }

                        if (
                            ResolveProgram(name: blend.A) is { } programA &&
                            ResolveProgram(name: blend.B) is { } programB
                        ) {
                            var resolvedA = Evaluate(
                                anchor: in anchor,
                                clock: in clock,
                                depth: (depth + 1),
                                program: programA
                            );
                            var resolvedB = Evaluate(
                                anchor: in anchor,
                                clock: in clock,
                                depth: (depth + 1),
                                program: programB
                            );
                            var weight = Math.Clamp(
                                value: blend.Weight.Resolve(
                                    definition: m_definition,
                                    fallback: 0f,
                                    tick: clock.AuthoritativeTick
                                ),
                                min: 0f,
                                max: 1f
                            );

                            eye = Vector3.Lerp(
                                amount: weight,
                                value1: resolvedA.Eye,
                                value2: resolvedB.Eye
                            );
                            target = Vector3.Lerp(
                                amount: weight,
                                value1: resolvedA.Target,
                                value2: resolvedB.Target
                            );
                            fov = float.Lerp(
                                value1: resolvedA.FovRadians,
                                value2: resolvedB.FovRadians,
                                amount: weight
                            );
                            haveTarget = true;
                        }

                        break;
                }
            }

            if (!haveTarget) {
                target = (eye + (Vector3.Transform(
                    value: -Vector3.UnitZ,
                    rotation: subject.Orientation
                ) * 6f));
            }

            return (eye, target, fov);
        }
        private WorldCameraProgram? ResolveProgram(string name) {
            if (string.IsNullOrEmpty(value: name)) {
                return null;
            }

            var views = m_definition.ViewsRaw;

            if (
                (views?.SeatRig is { } seatRig) &&
                string.Equals(
                a: seatRig.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return seatRig;
            }

            if (
                (views?.CameraRig is { } cameraRig) &&
                string.Equals(
                a: cameraRig.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return cameraRig;
            }

            foreach (var camera in m_definition.Cameras) {
                if (string.Equals(
                    a: camera.Rig.Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return camera.Rig;
                }
            }

            return null;
        }
    }
}
/// <summary>An anchor source that resolves one fixed pose for every id.</summary>
public sealed class FixedAnchorSource(SdfAnchor anchor) : ISdfAnchorSource {
    private SdfAnchor m_anchor = anchor;

    /// <summary>Repoints the fixed pose.</summary>
    /// <param name="anchor">The new pose.</param>
    public void Set(SdfAnchor anchor) => m_anchor = anchor;
    /// <inheritdoc/>
    public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
        anchor = m_anchor;

        return true;
    }
}
