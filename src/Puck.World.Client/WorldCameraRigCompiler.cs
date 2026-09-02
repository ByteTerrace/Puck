using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World;

/// <summary>A compiled camera program bound to a live document — the seam between the authored
/// <see cref="WorldCameraProgram"/> vocabulary and the document-blind evaluator in <c>Puck.SdfVm.Views</c>.</summary>
public interface IWorldCameraProgramRig : ISdfCameraRig {
    /// <summary>Gets or sets the live look sample folded into an interactive program's orbit, in radians. Inert on a
    /// program compiled non-interactive (a named camera renders its authored angles unchanged).</summary>
    SdfCameraLook Look { get; set; }
    /// <summary>Gets or sets the group spread an authored <see cref="WorldCameraProgramOp.Offset.SpreadPullback"/>
    /// widens by. Inert for a program authoring no pullback.</summary>
    float Spread { get; set; }
    /// <summary>Gets the response the last resolve reported (the program's
    /// <see cref="WorldCameraProgramOp.Dynamics"/>, or <see cref="SdfCameraDynamics.None"/>).</summary>
    SdfCameraDynamics Dynamics { get; }

    /// <summary>Repoints this rig's document reads at the current live document.</summary>
    /// <param name="definition">The current document.</param>
    /// <remarks>A cached rig must be retargeted whenever a delivery replaces the document: a state binding and a
    /// placement subject both read the LIVE document, never the one this rig compiled against.</remarks>
    void Retarget(WorldDefinition definition);
}
/// <summary>Compiles an authored camera program into one presentation rig: authored ops become
/// <see cref="SdfCameraOp"/>s, authored subjects and state bindings become per-frame slots this rig refills from the
/// live document, and the walk itself belongs to <see cref="SdfCameraProgramEvaluator"/>, which parses no
/// document.</summary>
public static class WorldCameraRigCompiler {
    /// <summary>Returns the program's authored eye or pivot position, for a caller that narrates or places a camera
    /// row rather than framing one — the orbit's resolved offset from its pivot, the offset op's raw value, or the
    /// origin for a program authoring neither.</summary>
    /// <param name="program">The authored program.</param>
    /// <returns>The authored position.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    public static Vector3 AuthoredPosition(WorldCameraProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        if (program.OrbitOp is { } orbit) {
            // A bound angle has no authored position to narrate; its literal (or the rest angle) stands in.
            return ((orbit.PivotOffset?.Value ?? Vector3.Zero) + OrbitRig.Offset(
                distance: orbit.Distance,
                pitch: (orbit.Pitch.Literal ?? 0f),
                yaw: (orbit.Yaw.Literal ?? 0f)
            ));
        }

        return (program.OffsetOp?.Value.Value ?? Vector3.Zero);
    }
    /// <summary>Compiles an authored camera program.</summary>
    /// <param name="program">The authored op list.</param>
    /// <param name="definition">The document this program's state bindings, placement subjects, and blend names
    /// resolve against.</param>
    /// <param name="interactive">Whether the program's orbit op folds in <see cref="IWorldCameraProgramRig.Look"/> —
    /// true for the seat rig a joined seat steers, false for an authored camera that renders its own angles.</param>
    /// <returns>A fresh presentation rig.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IWorldCameraProgramRig Compile(WorldCameraProgram program, WorldDefinition definition, bool interactive = false) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: program);

        var translation = new Translation(
            definition: definition,
            interactive: interactive
        );

        _ = translation.Translate(program: program);

        return new CompiledRig(
            definition: definition,
            scalarSources: translation.ScalarSources,
            set: new SdfCameraProgramSet(Programs: translation.Programs),
            subjectSources: translation.SubjectSources
        );
    }

    /// <summary>One compiled-rig cache slot: <see cref="Resolve"/> recompiles only when the authored program
    /// instance or a definition collection <see cref="Compile"/> reads has been replaced, otherwise the far cheaper
    /// <see cref="IWorldCameraProgramRig.Retarget"/>. Dynamics coefficients, a Path op's compiled curve, and
    /// Blend/Select program references are baked in at translate time — never re-read on retarget — so every
    /// collection they resolve against (<c>cameras</c>, <c>curves</c>, <c>dynamics</c>, and <c>views</c> for the
    /// seat/camera rig names) is part of the key. KEEP IN SYNC: only each section's own compose path may reuse its
    /// list's reference across deliveries; anything that clones one unconditionally defeats this check.</summary>
    public sealed class Cache {
        private IReadOnlyList<WorldCamera>? m_cameras;
        private IReadOnlyList<WorldCurveRow>? m_curves;
        private IReadOnlyList<WorldDynamicsRow>? m_dynamics;
        private bool m_interactive;
        private WorldCameraProgram? m_program;
        private IWorldCameraProgramRig? m_rig;
        private WorldViewDefaults? m_views;

        /// <summary>Returns the cached rig retargeted at the live document, or a fresh <see cref="Compile"/> when
        /// any input it read has moved.</summary>
        /// <param name="program">The authored op list.</param>
        /// <param name="definition">The current live document.</param>
        /// <param name="interactive">Whether the program's orbit op folds in the live look (see
        /// <see cref="Compile"/>).</param>
        /// <returns>The rig.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        public IWorldCameraProgramRig Resolve(WorldCameraProgram program, WorldDefinition definition, bool interactive = false) {
            ArgumentNullException.ThrowIfNull(argument: definition);
            ArgumentNullException.ThrowIfNull(argument: program);

            if (
                (m_rig is { } rig) &&
                (m_interactive == interactive) &&
                ReferenceEquals(
                    objA: m_program,
                    objB: program
                ) &&
                ReferenceEquals(
                    objA: m_cameras,
                    objB: definition.Cameras
                ) &&
                ReferenceEquals(
                    objA: m_curves,
                    objB: definition.Curves
                ) &&
                ReferenceEquals(
                    objA: m_dynamics,
                    objB: definition.Dynamics
                ) &&
                ReferenceEquals(
                    objA: m_views,
                    objB: definition.ViewsRaw
                )
            ) {
                rig.Retarget(definition: definition);

                return rig;
            }

            m_cameras = definition.Cameras;
            m_curves = definition.Curves;
            m_dynamics = definition.Dynamics;
            m_interactive = interactive;
            m_program = program;
            m_views = definition.ViewsRaw;
            m_rig = Compile(
                definition: definition,
                interactive: interactive,
                program: program
            );

            return m_rig;
        }
    }

    // One per-frame scalar slot's source: an authored state binding read at the frame's tick, or the group-spread
    // widening an offset op's pullback applies. Exactly one arm is live per slot.
    private readonly record struct ScalarSource(BindableScalar? Binding, float Fallback, float SpreadPullback);
    // One per-frame subject slot's source — an authored subject other than the program's own reference pose.
    private readonly record struct SubjectSource(WorldCameraSubject Subject);
    // The authored-to-IR walk. Programs are keyed by authored NAME so a blend that reaches the same program twice
    // (and a cycle the validator would have refused) compiles to one entry rather than recursing forever.
    private sealed class Translation(WorldDefinition definition, bool interactive) {
        private readonly Dictionary<string, int> m_indexByName = new(comparer: StringComparer.Ordinal);

        public List<SdfCameraProgram> Programs { get; } = [];
        public List<ScalarSource> ScalarSources { get; } = [];
        public List<SubjectSource> SubjectSources { get; } = [];

        public int Translate(WorldCameraProgram program) {
            var name = (program.Name ?? string.Empty);

            if (m_indexByName.TryGetValue(
                key: name,
                value: out var existing
            )) {
                return existing;
            }

            var index = Programs.Count;

            m_indexByName[name] = index;
            // Reserve the slot BEFORE walking, so a blend reaching back into this program resolves to it.
            Programs.Add(item: new SdfCameraProgram(
                Name: name,
                Operations: []
            ));
            Programs[index] = new SdfCameraProgram(
                Name: name,
                Operations: TranslateOperations(program: program)
            );

            return index;
        }

        private SdfCameraScalar Scalar(BindableScalar scalar, float fallback) {
            if (scalar.Binding is null) {
                return SdfCameraScalar.FromLiteral(value: (((scalar.Literal is { } literal) && float.IsFinite(f: literal))
                    ? literal
                    : fallback));
            }

            var slot = ScalarSources.Count;

            ScalarSources.Add(item: new ScalarSource(
                Binding: scalar,
                Fallback: fallback,
                SpreadPullback: 0f
            ));

            return SdfCameraScalar.FromSlot(
                fallback: fallback,
                slot: slot
            );
        }
        private SdfCameraScalar SpreadScale(float pullback) {
            if (pullback == 0f) {
                return SdfCameraScalar.FromLiteral(value: 1f);
            }

            var slot = ScalarSources.Count;

            ScalarSources.Add(item: new ScalarSource(
                Binding: null,
                Fallback: 1f,
                SpreadPullback: pullback
            ));

            return SdfCameraScalar.FromSlot(
                fallback: 1f,
                slot: slot
            );
        }
        private int Subject(WorldCameraSubject? subject) {
            if (subject is null or WorldCameraSubject.Reference) {
                return SdfCameraProgram.ReferenceSubject;
            }

            var slot = SubjectSources.Count;

            SubjectSources.Add(item: new SubjectSource(Subject: subject));

            return slot;
        }
        private List<SdfCameraOp> TranslateOperations(WorldCameraProgram program) {
            var authored = program.Operations;
            var operations = new List<SdfCameraOp>(capacity: authored.Count);

            for (var index = 0; (index < authored.Count); index++) {
                switch (authored[index]) {
                    case WorldCameraProgramOp.Anchor anchor:
                        operations.Add(item: new SdfCameraOp.Anchor(SubjectSlot: Subject(subject: anchor.Subject)));

                        break;
                    case WorldCameraProgramOp.Offset offset:
                        operations.Add(item: new SdfCameraOp.Offset(
                            Scale: SpreadScale(pullback: offset.SpreadPullback),
                            Value: offset.Value.Value,
                            WorldAxes: offset.WorldAxes
                        ));

                        break;
                    case WorldCameraProgramOp.LookAt lookAt:
                        operations.Add(item: new SdfCameraOp.LookAt(
                            FocusDistance: SdfCameraScalar.FromLiteral(value: lookAt.FocusDistance),
                            SubjectSlot: ((lookAt.Subject is { } lookSubject)
                            ? Subject(subject: lookSubject)
                            : SdfCameraProgram.FacingSubject),
                            TargetOffset: (lookAt.TargetOffset?.Value ?? Vector3.Zero),
                            WorldAxes: lookAt.WorldAxes
                        ));

                        break;
                    case WorldCameraProgramOp.Orbit orbit:
                        operations.Add(item: new SdfCameraOp.Orbit(
                            AppliesLook: interactive,
                            Distance: SdfCameraScalar.FromLiteral(value: orbit.Distance),
                            Pitch: Scalar(
                                fallback: 0f,
                                scalar: orbit.Pitch
                            ),
                            PivotOffset: (orbit.PivotOffset?.Value ?? Vector3.Zero),
                            Yaw: Scalar(
                                fallback: 0f,
                                scalar: orbit.Yaw
                            )
                        ));

                        break;
                    case WorldCameraProgramOp.Path pathOp:
                        // A row a mid-mutation document no longer declares emits no op, the same rule Dynamics/Blend
                        // follow below — the validator refuses a dangling row at author time, so this can only
                        // transiently miss during a live document swap.
                        if (WorldDefinitionRows.FindCurve(
                            curves: definition.Curves,
                            name: pathOp.Curve
                        ) is { } curveRow) {
                            operations.Add(item: new SdfCameraOp.Path(
                                Curve: new SdfCurvePath(compiled: curveRow.Compiled),
                                Fraction: Scalar(
                                    fallback: 0f,
                                    scalar: pathOp.Fraction
                                )
                            ));
                        }

                        break;
                    case WorldCameraProgramOp.Dynamics dynamicsOp:
                        // A row a mid-mutation document no longer declares emits no op, the same rule Blend's
                        // dangling-name case follows below — the validator refuses a dangling row at author time, so
                        // this can only transiently miss during a live document swap.
                        if (WorldDefinitionRows.FindDynamics(
                            dynamics: definition.Dynamics,
                            name: dynamicsOp.Row
                        ) is { } row) {
                            var parameters = row.Parameters;

                            operations.Add(item: new SdfCameraOp.Dynamics(Value: new SdfCameraDynamics(
                                Damping: parameters.Damping,
                                Frequency: parameters.Frequency,
                                Response: parameters.Response
                            )));
                        }

                        break;
                    case WorldCameraProgramOp.ClampPitch clampPitch:
                        operations.Add(item: new SdfCameraOp.ClampPitch(
                            MaxPitch: SdfCameraScalar.FromLiteral(value: clampPitch.MaxPitch),
                            MinPitch: SdfCameraScalar.FromLiteral(value: clampPitch.MinPitch)
                        ));

                        break;
                    case WorldCameraProgramOp.Fov fov:
                        operations.Add(item: new SdfCameraOp.Fov(FieldOfViewRadians: Scalar(
                            fallback: OrbitRig.DefaultFieldOfViewRadians,
                            scalar: fov.FieldOfViewRadians
                        )));

                        break;
                    case WorldCameraProgramOp.Blend blend:
                        // A name the live document no longer declares emits no op: the validator refuses a dangling
                        // blend at author time, so this can only be a mid-mutation document, and rendering the
                        // program's own framing beats blending against a pose nothing authored.
                        if (
                            (ResolveProgram(name: blend.A) is { } programA) &&
                            (ResolveProgram(name: blend.B) is { } programB)
                        ) {
                            operations.Add(item: new SdfCameraOp.Blend(
                                ProgramA: Translate(program: programA),
                                ProgramB: Translate(program: programB),
                                Weight: Scalar(
                                    fallback: 0f,
                                    scalar: blend.Weight
                                )
                            ));
                        }

                        break;
                    case WorldCameraProgramOp.Select selectOp:
                        // Same conservative rule as Blend: a document a live mutation left mid-transition can only
                        // transiently miss a resolved name (the validator refuses one dangling at author time), and
                        // dropping the whole op there beats resolving into a pose nothing authored.
                        if (ResolveProgram(name: selectOp.Default) is not { } defaultProgram) {
                            break;
                        }

                        var cases = new List<SdfCameraSelectCase>(capacity: selectOp.Cases.Count);
                        var everyCaseResolved = true;

                        foreach (var candidate in selectOp.Cases) {
                            if (ResolveProgram(name: candidate.Program) is not { } caseProgram) {
                                everyCaseResolved = false;

                                break;
                            }

                            cases.Add(item: new SdfCameraSelectCase(
                                Program: Translate(program: caseProgram),
                                Value: candidate.Value
                            ));
                        }

                        if (!everyCaseResolved) {
                            break;
                        }

                        operations.Add(item: new SdfCameraOp.Select(
                            Cases: cases,
                            DefaultProgram: Translate(program: defaultProgram),
                            Key: Scalar(
                                fallback: 0f,
                                scalar: selectOp.Key
                            )
                        ));

                        break;
                }
            }

            return operations;
        }
        // The document-wide camera-program name table a blend op resolves against: views.seatRig, views.cameraRig,
        // and every cameras[].rig — the same namespace WorldDefinitionValidator walks for dangling names and cycles.
        private WorldCameraProgram? ResolveProgram(string name) {
            if (string.IsNullOrEmpty(value: name)) {
                return null;
            }

            var views = definition.ViewsRaw;

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

            foreach (var camera in definition.Cameras) {
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
    private sealed class CompiledRig : IWorldCameraProgramRig {
        private readonly SdfCameraProgramRig m_rig;
        private readonly IReadOnlyList<ScalarSource> m_scalarSources;
        private readonly IReadOnlyList<SubjectSource> m_subjectSources;

        private WorldDefinition m_definition;

        public CompiledRig(SdfCameraProgramSet set, WorldDefinition definition, IReadOnlyList<ScalarSource> scalarSources, IReadOnlyList<SubjectSource> subjectSources) {
            m_definition = definition;
            m_rig = new SdfCameraProgramRig(
                programs: set,
                scalarCount: scalarSources.Count,
                subjectCount: subjectSources.Count
            );
            m_scalarSources = scalarSources;
            m_subjectSources = subjectSources;
        }

        public SdfCameraDynamics Dynamics => m_rig.Dynamics;
        public SdfCameraLook Look {
            get => m_rig.Look;
            set => m_rig.Look = value;
        }
        public float Spread { get; set; }

        public void Retarget(WorldDefinition definition) {
            ArgumentNullException.ThrowIfNull(argument: definition);

            m_definition = definition;
        }
        public (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, in SdfCameraClock clock) {
            Refresh(tick: clock.AuthoritativeTick);

            return m_rig.Resolve(
                anchor: in anchor,
                clock: in clock
            );
        }

        // Refills the evaluator's per-frame slots from the live document. Runs inside Resolve so no caller can
        // evaluate against a stale binding by forgetting an ordering step.
        private void Refresh(ulong tick) {
            var scalars = m_rig.Scalars;

            for (var index = 0; (index < m_scalarSources.Count); index++) {
                var source = m_scalarSources[index];

                scalars[index] = ((source.Binding is { } binding)
                    ? binding.Resolve(
                        definition: m_definition,
                        fallback: source.Fallback,
                        tick: tick
                    )
                    : (1f + (source.SpreadPullback * MathF.Max(
                        x: Spread,
                        y: 0f
                    )))
                );
            }

            var subjects = m_rig.Subjects;

            for (var index = 0; (index < m_subjectSources.Count); index++) {
                subjects[index] = (m_subjectSources[index].Subject switch {
                    WorldCameraSubject.Placement placement => new SdfAnchor(
                        Orientation: Quaternion.Identity,
                        Position: WorldAnchorGeometry.StaticPlacementPosition(
                            definition: m_definition,
                            placementId: placement.PlacementId,
                            shapeId: placement.ShapeId
                        )
                    ),
                    WorldCameraSubject.WorldPoint worldPoint => new SdfAnchor(
                        Orientation: Quaternion.Identity,
                        Position: worldPoint.Point.Value
                    ),
                    _ => default,
                });
            }
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
