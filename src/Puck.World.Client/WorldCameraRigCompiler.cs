using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World;

/// <summary>Compiles authored camera motion, aim, and lens axes into one presentation rig.</summary>
public static class WorldCameraRigCompiler {
    /// <summary>Gets the motion's authored eye or pivot position for editor targeting.</summary>
    public static Vector3 AuthoredPosition(WorldCameraMotion motion) => motion switch {
        WorldCameraMotion.Follow value => value.Offset,
        WorldCameraMotion.Orbit value => (value.PivotOffset + OrbitRig.Offset(
        yaw: value.Yaw,
        pitch: value.Pitch,
        distance: value.Distance
    )),
        WorldCameraMotion.Static value => value.Position,
        WorldCameraMotion.Track { Definition.Keyframes.Count: > 0 } value => value.Definition.Keyframes[0].Position,
        _ => Vector3.Zero,
    };
    /// <summary>Compiles an authored camera rig.</summary>
    /// <param name="rig">The authored axes.</param>
    /// <param name="referenceOffset">An additional reference-local offset, such as an entity-leaf rest offset.</param>
    /// <param name="spread">The resolved group spread.</param>
    /// <returns>A fresh presentation rig.</returns>
    public static ISdfCameraRig Compile(WorldCameraRig rig, Vector3 referenceOffset = default, float spread = 0f) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        return new ComposedWorldCameraRig(
            referenceOffset: referenceOffset,
            rig: rig,
            spread: spread
        );
    }
    /// <summary>Moves a camera motion axis without changing its aim or lens.</summary>
    public static WorldCameraMotion Move(WorldCameraMotion motion, Vector3 value, bool relative) {
        var current = AuthoredPosition(motion: motion);
        var delta = (relative
            ? value
            : (value - current)
        );

        return motion switch {
            WorldCameraMotion.Follow follow => follow with { Offset = (follow.Offset + delta) },
            WorldCameraMotion.Orbit orbit => orbit with { PivotOffset = (orbit.PivotOffset + delta) },
            WorldCameraMotion.Static position => position with { Position = (position.Position + delta) },
            WorldCameraMotion.Track track => track with {
                Definition = track.Definition with {
                    Keyframes = track.Definition.Keyframes.Select(selector: keyframe => keyframe with { Position = (keyframe.Position + delta) }).ToArray(),
                },
            },
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(motion),
            actualValue: motion,
            message: "unknown camera motion kind."
        ),
        };
    }

    private sealed class ComposedWorldCameraRig(WorldCameraRig rig, Vector3 referenceOffset, float spread) : ISdfCameraRig {
        private static Vector3 EvaluateTrack(WorldCameraMotion.Track track, in SdfCameraClock clock) {
            var definition = track.Definition;
            var keyframes = definition.Keyframes;
            var clockTick = ((definition.ClockDomain == WorldCameraTrackClockDomain.PresentationTime)
                ? (((double)clock.PresentationSeconds) * 240.0)
                : clock.AuthoritativeTick
            );
            var elapsed = Math.Max(
                val1: 0.0,
                val2: (clockTick - track.Playback.StartTick)
            );
            var first = keyframes[0];
            var last = keyframes[^1];
            var duration = ((double)(last.Tick - first.Tick));

            if (duration <= 0.0) {
                return last.Position;
            }

            var localTick = (elapsed + first.Tick);

            switch (track.Playback.LoopMode) {
                case WorldCameraTrackLoopMode.Once:
                    localTick = Math.Min(
                        val1: localTick,
                        val2: last.Tick
                    );
                    break;
                case WorldCameraTrackLoopMode.Loop:
                    localTick = (first.Tick + (elapsed % duration));
                    break;
                case WorldCameraTrackLoopMode.PingPong:
                    var phase = (elapsed % (duration * 2.0));
                    localTick = (first.Tick + ((phase <= duration)
                        ? phase
                        : ((duration * 2.0) - phase)));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        paramName: nameof(track),
                        actualValue: track.Playback.LoopMode,
                        message: "unknown camera track loop mode."
                    );
            }

            for (var index = 1; (index < keyframes.Count); index++) {
                var right = keyframes[index];

                if (localTick > right.Tick) {
                    continue;
                }

                var left = keyframes[(index - 1)];

                if (definition.Interpolation == WorldCameraTrackInterpolation.Step) {
                    return left.Position;
                }
                var amount = ((float)((localTick - left.Tick) / (right.Tick - left.Tick)));

                return Vector3.Lerp(
                    value1: left.Position,
                    value2: right.Position,
                    amount: amount
                );
            }

            return last.Position;
        }
        private static Vector3 ResolveFollow(Vector3 referencePosition, Quaternion orientation, WorldCameraMotion.Follow follow, float spread) {
            var offset = (follow.Offset * (1f + (follow.SpreadPullback * MathF.Max(
                x: spread,
                y: 0f
            ))));

            return ResolveOffset(
                referencePosition: referencePosition,
                orientation: orientation,
                value: offset,
                worldAxes: follow.WorldAxes
            );
        }
        private static Vector3 ResolveOffset(Vector3 referencePosition, Quaternion orientation, Vector3 value, bool worldAxes) =>
            (referencePosition + (worldAxes
                ? value
                : Vector3.Transform(
                    rotation: orientation,
                    value: value
                )));
        private static Vector3 ResolveOrbit(Vector3 referencePosition, WorldCameraMotion.Orbit orbit) {
            var pivot = (referencePosition + orbit.PivotOffset);

            return (pivot + OrbitRig.Offset(
                yaw: orbit.Yaw,
                pitch: orbit.Pitch,
                distance: orbit.Distance
            ));
        }
        private static Vector3 ResolvePosition(Vector3 referencePosition, Quaternion orientation, Vector3 value, bool worldAxes) =>
            (worldAxes
                ? value
                : (referencePosition + Vector3.Transform(
                    rotation: orientation,
                    value: value
                ))
            );

        public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, in SdfCameraClock clock) {
            var referencePosition = (anchor.Position + Vector3.Transform(
                value: referenceOffset,
                rotation: anchor.Orientation
            ));
            var eye = rig.Motion switch {
                WorldCameraMotion.FirstPerson => referencePosition,
                WorldCameraMotion.Follow follow => ResolveFollow(
                referencePosition: referencePosition,
                orientation: anchor.Orientation,
                follow: follow,
                spread: spread
            ),
                WorldCameraMotion.Orbit orbit => ResolveOrbit(
                orbit: orbit,
                referencePosition: referencePosition
            ),
                WorldCameraMotion.Static value => ResolvePosition(
                referencePosition: referencePosition,
                orientation: anchor.Orientation,
                value: value.Position,
                worldAxes: value.WorldAxes
            ),
                WorldCameraMotion.Track track => ResolvePosition(
                referencePosition: referencePosition,
                orientation: anchor.Orientation,
                value: EvaluateTrack(
                    clock: in clock,
                    track: track
                ),
                worldAxes: track.WorldAxes
            ),
                var motion => throw new ArgumentOutOfRangeException(
                paramName: nameof(rig),
                actualValue: motion,
                message: $"unknown camera motion kind '{(motion?.GetType().Name ?? "<null>")}'."
            ),
            };
            var target = rig.Aim switch {
                WorldCameraAim.Anchor value => ResolveOffset(
                referencePosition: referencePosition,
                orientation: anchor.Orientation,
                value: value.Offset,
                worldAxes: value.WorldAxes
            ),
                WorldCameraAim.Forward value => (eye + (Vector3.Transform(
                value: -Vector3.UnitZ,
                rotation: anchor.Orientation
            ) * MathF.Max(
                x: value.FocusDistance,
                y: 0.01f
            ))),
                WorldCameraAim.WorldPoint value => value.Target.Value,
                var aim => throw new ArgumentOutOfRangeException(
                paramName: nameof(rig),
                actualValue: aim,
                message: $"unknown camera aim kind '{(aim?.GetType().Name ?? "<null>")}'."
            ),
            };

            return (eye, target, rig.Lens.FieldOfViewRadians);
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
