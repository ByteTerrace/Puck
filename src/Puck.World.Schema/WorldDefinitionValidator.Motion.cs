using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void RequireNonNegativeScalar(BodyProgramParameters parameters, string name, string path, List<string> errors) {
        if (TryScalar(
            name: name,
            parameters: parameters,
            value: out var value
        )) {
            RequireNonNegative(
                errors: errors,
                name: $"{path}.scalars[{name}]",
                value: value
            );
        }
    }
    private static void RequirePositiveScalar(BodyProgramParameters parameters, string name, string path, List<string> errors) {
        if (TryScalar(
            name: name,
            parameters: parameters,
            value: out var value
        )) {
            RequirePositive(
                errors: errors,
                name: $"{path}.scalars[{name}]",
                value: value
            );
        }
    }
    // The op→facet mapping: walks a COMPILED program's selected operations (never the authored list — compilation
    // already rejected an unknown/inadmissible opcode) and unions the facets each one reads. Speed is unconditional
    // for every Motion-kind program (WorldBody resolves MoveSpeed/TurnSpeed before op dispatch, independent of which
    // operations a program selects) — callers only reach here once the kit's program has already been confirmed
    // Motion-kind (see ValidateKits).
    private static MotionTuningFacet RequiredMotionTuningFacets(CompiledBodyMotionProgram program) {
        var facets = MotionTuningFacet.Speed;

        foreach (var op in Enum.GetValues<BodyMotionOp>()) {
            if (!program.Contains(operation: op)) {
                continue;
            }

            facets |= op switch {
                BodyMotionOp.ApplyVerticalGravity => MotionTuningFacet.GravityArc,
                BodyMotionOp.ApplyVerticalDecay => MotionTuningFacet.GravityBleed,
                BodyMotionOp.ShapePlanarVelocity => MotionTuningFacet.PlanarResponse,
                BodyMotionOp.ComputePlanarTargetVelocity => MotionTuningFacet.Sprint,
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame or BodyMotionOp.SnapYawToPlanarIntent => MotionTuningFacet.WorldFrame,
                BodyMotionOp.ResolveDriveFrame or BodyMotionOp.ShapeDriveVelocity => MotionTuningFacet.Drive,
                BodyMotionOp.ResolveHold or BodyMotionOp.ApplyHold => (MotionTuningFacet.GravityArc | MotionTuningFacet.Holds),
                _ => MotionTuningFacet.None,
            };
        }

        return facets;
    }
    // What a kit's motion row supplies. Its own fields supply every facet unconditionally; Drive is the one an
    // OPTIONAL row carries, so a kit authoring none refuses a drive program by facet name.
    private static MotionTuningFacet SuppliedMotionTuningFacets(WorldMotion motion) => (MotionTuningFacet.Speed | MotionTuningFacet.GravityArc | MotionTuningFacet.GravityBleed
        | MotionTuningFacet.PlanarResponse | MotionTuningFacet.Sprint | MotionTuningFacet.WorldFrame | MotionTuningFacet.Holds
        | ((motion.Drive is not null)
        ? MotionTuningFacet.Drive
        : MotionTuningFacet.None));
    private static bool TryScalar(BodyProgramParameters parameters, string name, out float value) => parameters.Scalars.TryGetValue(
        key: name,
        value: out value
    );
    // The authored rows keyed the same way the compiled table is (first row of a duplicated name wins, matching
    // ValidateBodyMotionPrograms). The target source a producer senses is authored vocabulary rather than compiled
    // instruction state, so the checks over it read the row rather than the compiled program.
    private static Dictionary<string, BodyMotionProgram> BodyMotionProgramRows(IReadOnlyList<BodyMotionProgram> programs) {
        var rows = new Dictionary<string, BodyMotionProgram>(comparer: StringComparer.Ordinal);

        foreach (var program in programs) {
            if (program is { Name: not null }) {
                _ = rows.TryAdd(
                    key: program.Name,
                    value: program
                );
            }
        }

        return rows;
    }
    private static Dictionary<string, CompiledBodyMotionProgram> ValidateBodyMotionPrograms(IReadOnlyList<BodyMotionProgram> programs, ISet<string> targetRegisterNames, ISet<string> navigationDomainNames, ISet<string> curveNames, int simulationRateHz, List<string> errors) {
        var compiled = new Dictionary<string, CompiledBodyMotionProgram>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < programs.Count); index++) {
            var program = programs[index];
            var path = $"bodyMotionPrograms[{index}]";

            if (program is null) {
                errors.Add(item: $"{path} is required.");
                continue;
            }
            if (compiled.ContainsKey(key: program.Name)) {
                errors.Add(item: $"{path}.name '{program.Name}' is duplicated.");
                continue;
            }

            try {
                var compiledProgram = BodyMotionProgramFactory.Compile(program: program);

                compiled.Add(
                    key: program.Name,
                    value: compiledProgram
                );

                if (compiledProgram.Kind == BodyProgramKind.Producer) {
                    var senses = compiledProgram.Contains(operation: BodyMotionOp.SenseNearestInCone);

                    if (
                        compiledProgram.Contains(operation: BodyMotionOp.ProduceAttendIntent) &&
                        !senses
                    ) {
                        errors.Add(item: $"{path} producer opcode '{BodyMotionOp.ProduceAttendIntent}' requires '{BodyMotionOp.SenseNearestInCone}'.");
                    }
                    if (
                        compiledProgram.Contains(operation: BodyMotionOp.FaceSensorTarget) &&
                        !compiledProgram.Contains(operation: BodyMotionOp.ProduceAttendIntent)
                    ) {
                        errors.Add(item: $"{path} producer opcode '{BodyMotionOp.FaceSensorTarget}' requires '{BodyMotionOp.ProduceAttendIntent}'.");
                    }
                    if (
                        senses &&
                        (program.Target is null)
                    ) {
                        errors.Add(item: $"{path}.target is required by '{BodyMotionOp.SenseNearestInCone}'.");
                    } else if (
                        !senses &&
                        (program.Target is not null)
                    ) {
                        errors.Add(item: $"{path}.target requires '{BodyMotionOp.SenseNearestInCone}'.");
                    }
                    switch (program.Target) {
                        case BodyTargetSource.Sensed sensed:
                            if (!Enum.IsDefined(value: sensed.Scope)) {
                                errors.Add(item: $"{path}.target.scope '{sensed.Scope}' is not a defined BodyTargetScope.");
                            }
                            RequirePositive(
                                value: sensed.Range,
                                name: $"{path}.target.range",
                                errors: errors
                            );
                            RequireRange(
                                value: sensed.HalfAngleDegrees,
                                min: 0f,
                                max: 180f,
                                name: $"{path}.target.halfAngleDegrees",
                                errors: errors,
                                minExclusive: true
                            );
                            break;
                        case BodyTargetSource.Designated designated when (string.IsNullOrWhiteSpace(value: designated.Register) || !targetRegisterNames.Contains(item: designated.Register)):
                            errors.Add(item: $"{path}.target.register '{designated.Register}' names no target register.");
                            break;
                        case BodyTargetSource.CurveFollow curve:
                            RequireDeclared(
                                value: curve.Curve,
                                declaredSet: curveNames,
                                path: path,
                                field: "target.curve",
                                rowNoun: "curves",
                                errors: errors
                            );
                            RequireRange(
                                value: curve.Rate,
                                min: -WorldCurves.MaxFollowRate,
                                max: WorldCurves.MaxFollowRate,
                                name: $"{path}.target.rate",
                                errors: errors
                            );

                            if (simulationRateHz <= 0) {
                                errors.Add(item: $"{path}.target.curve '{curve.Curve}' cannot compile — the world authors no simulation rate (simulation.rateHz), and a curve-follow target's per-tick arc step is bound to one.");
                            }

                            break;
                        case BodyTargetSource.Navigated navigated:
                            if (string.IsNullOrWhiteSpace(value: navigated.Domain) || !navigationDomainNames.Contains(item: navigated.Domain)) {
                                errors.Add(item: $"{path}.target.domain '{navigated.Domain}' names no navigation domain.");
                            }
                            if (string.IsNullOrWhiteSpace(value: navigated.Register) || !targetRegisterNames.Contains(item: navigated.Register)) {
                                errors.Add(item: $"{path}.target.register '{navigated.Register}' names no target register.");
                            }
                            break;
                    }
                } else if (program.Target is not null) {
                    errors.Add(item: $"{path}.target is only admitted on a Producer program.");
                }
            } catch (BodyMotionProgramException exception) {
                errors.Add(item: $"{path} {exception.Message}");
            }
        }

        return compiled;
    }
    // The shape every authored envelope must have: min/max finite, min <= max (FixedQ4816.Clamp's
    // own precondition, refused here so it never throws at seat-resolve time), min non-negative — every consumer
    // bounds a speed magnitude, and reverse travel is its own non-negative scalar (drive.reverseSpeed), so a negative
    // endpoint would only widen the clamp past the bound's apparent intent. Returns whether the shape held, so a
    // caller layering an additional check can skip it once the bound is already malformed.
    private static bool ValidateEnvelopeShape(MotionScalarEnvelope envelope, string path, List<string> errors) {
        if (
            !float.IsFinite(f: envelope.Min) ||
            !float.IsFinite(f: envelope.Max)
        ) {
            errors.Add(item: $"{path} must have a finite min and max.");

            return false;
        }

        if (envelope.Min > envelope.Max) {
            errors.Add(item: $"{path}.min ({envelope.Min}) is greater than {path}.max ({envelope.Max}).");

            return false;
        }

        if (envelope.Min < 0f) {
            errors.Add(item: $"{path}.min ({envelope.Min}) is negative — an envelope bounds a speed magnitude, so a negative endpoint admits magnitudes past the bound's own max; reverse travel is authored as its own positive scalar, never a negative speed.");

            return false;
        }

        return true;
    }
    // A kit's full locomotion tuning: speeds, gravity, and the velocity-response table every body integrates under.
    private static void ValidateMotion(WorldMotion tuning, string path, ISet<string> channelNames, ISet<string> dynamicsNames, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, bool hasMedium, bool shapesPlanarVelocity, int simulationRateHz, List<string> errors) {
        RequirePositive(
            value: tuning.MoveSpeed,
            name: $"{path}.moveSpeed",
            errors: errors
        );
        RequirePositive(
            value: tuning.TurnSpeed,
            name: $"{path}.turnSpeed",
            errors: errors
        );
        RequirePositive(
            value: tuning.RiseGravity,
            name: $"{path}.riseGravity",
            errors: errors
        );
        RequirePositive(
            value: tuning.FallGravity,
            name: $"{path}.fallGravity",
            errors: errors
        );
        RequirePositive(
            value: tuning.MaxFallSpeed,
            name: $"{path}.maxFallSpeed",
            errors: errors
        );
        RequirePositive(
            value: tuning.SprintMultiplier,
            name: $"{path}.sprintMultiplier",
            errors: errors
        );
        if (shapesPlanarVelocity) {
            ValidatePlanarShaping(
                dynamics: tuning.Dynamics,
                dynamicsNames: dynamicsNames,
                errors: errors,
                path: path,
                response: tuning.Response,
                simulationRateHz: simulationRateHz
            );
        }

        // The sprint channel needs the same "must resolve" bar
        // ValidateRoute holds engageChannel to, for the identical reason (a misspelled name would otherwise be a
        // silent, permanent no-op — the button never sprints and nothing here would have said why).
        if (
            (tuning.SprintChannel is { Length: > 0 } sprintChannel) &&
            !channelNames.Contains(item: sprintChannel)
        ) {
            errors.Add(item: $"{path}.sprintChannel '{sprintChannel}' names no declared composition channel.");
        }

        if (!Enum.IsDefined(value: tuning.MoveFrame)) {
            errors.Add(item: $"{path}.moveFrame '{tuning.MoveFrame}' is not a defined MotionMoveFrame.");
        }

        // Absent, this envelope is wide-open (unclamped). Another overridable scalar walks the same gate.
        if (tuning.MoveSpeedEnvelope is { } moveSpeedEnvelope) {
            ValidateScalarEnvelope(
                envelope: moveSpeedEnvelope,
                ownValue: tuning.MoveSpeed,
                ownValueName: "moveSpeed",
                path: $"{path}.moveSpeedEnvelope",
                errors: errors
            );
        }

        ValidateHolds(
            channelNames: channelNames,
            errors: errors,
            hasMedium: hasMedium,
            holds: tuning.Holds,
            path: $"{path}.holds",
            stateSlots: stateSlots
        );

        if (tuning.Drive is { } drive) {
            ValidateDriveMotion(
                channelNames: channelNames,
                errors: errors,
                path: $"{path}.drive",
                tuning: drive
            );
        }
    }
    // The ordered hold list: a unique name per row, a cone inside [0, 180] with min < max for a surface row (and no
    // cone at all for a free one), the kind's own operands, and every named channel/state slot resolvable. Absent (a
    // kit authoring none) validates nothing: the vertical channel is ApplyVerticalGravity's alone there.
    private static void ValidateHolds(IReadOnlyList<WorldHold>? holds, string path, ISet<string> channelNames, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, bool hasMedium, List<string> errors) {
        if (holds is not { Count: > 0 }) {
            return;
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < holds.Count); index++) {
            var hold = holds[index];
            var rowPath = $"{path}[{index}]";

            if (hold is null) {
                errors.Add(item: $"{rowPath} is required.");

                continue;
            }

            RequireUniqueName(
                errors: errors,
                field: "",
                path: rowPath,
                seen: names,
                value: hold.Name
            );

            if (!Enum.IsDefined(value: hold.Bond)) {
                errors.Add(item: $"{rowPath}.bond '{hold.Bond}' is not a defined BodyHoldBond.");
            }
            if (!Enum.IsDefined(value: hold.Hold)) {
                errors.Add(item: $"{rowPath}.hold '{hold.Hold}' is not a defined BodyHoldKind.");
            }
            if (!Enum.IsDefined(value: hold.Forward)) {
                errors.Add(item: $"{rowPath}.forward '{hold.Forward}' is not a defined BodyHoldForward.");
            }

            RequireRange(
                errors: errors,
                max: 1f,
                min: 0f,
                name: $"{rowPath}.upLean",
                value: hold.UpLean
            );
            RequireRange(
                errors: errors,
                max: 1f,
                min: 0f,
                name: $"{rowPath}.driveAlignment",
                value: hold.DriveAlignment
            );

            if (hold.Speed is { } speed) {
                RequirePositive(
                    errors: errors,
                    name: $"{rowPath}.speed",
                    value: speed
                );
            }
            if (hold.Bond == BodyHoldBond.Surface) {
                if (hold.Cone is not { } cone) {
                    errors.Add(item: $"{rowPath}.cone is required for a surface hold — the angle band, in degrees, between an admitted surface normal and gravity-up.");
                } else if (
                    !float.IsFinite(f: cone.X) ||
                    !float.IsFinite(f: cone.Y) ||
                    (cone.X < 0f) ||
                    (cone.Y > 180f) ||
                    (cone.X >= cone.Y)
                ) {
                    errors.Add(item: $"{rowPath}.cone [{cone.X}, {cone.Y}] must be finite, within [0, 180], and increasing.");
                }

                RequirePositive(
                    errors: errors,
                    name: $"{rowPath}.reach",
                    value: hold.Reach
                );
            } else if (hold.Cone is not null) {
                errors.Add(item: $"{rowPath}.cone is refused for a {hold.Bond} hold — only a surface hold has a face for a cone to admit.");
            }
            if (hold.Bond == BodyHoldBond.Medium) {
                if (!hasMedium) {
                    errors.Add(item: $"{rowPath}.bond 'Medium' requires a medium lattice row (state.world[].lattice.medium) — a medium hold implies a medium to stand in.");
                }
                if (hold.Medium is not { } medium) {
                    errors.Add(item: $"{rowPath}.medium is required for a medium hold — buoyancy, the terminal speeds, the settle rate, the float depth and the thrust fraction are its whole law.");
                } else {
                    RequirePositive(
                        errors: errors,
                        name: $"{rowPath}.medium.maxRiseSpeed",
                        value: medium.MaxRiseSpeed
                    );
                    RequirePositive(
                        errors: errors,
                        name: $"{rowPath}.medium.maxSinkSpeed",
                        value: medium.MaxSinkSpeed
                    );
                    RequirePositive(
                        errors: errors,
                        name: $"{rowPath}.medium.surfaceSettleRate",
                        value: medium.SurfaceSettleRate
                    );
                    RequirePositive(
                        errors: errors,
                        name: $"{rowPath}.medium.floatDepth",
                        value: medium.FloatDepth
                    );
                    RequirePositive(
                        errors: errors,
                        name: $"{rowPath}.medium.thrustFraction",
                        value: medium.ThrustFraction
                    );
                    RequireFinite(
                        errors: errors,
                        name: $"{rowPath}.medium.buoyancy",
                        value: medium.Buoyancy
                    );
                }
            } else if (hold.Medium is not null) {
                errors.Add(item: $"{rowPath}.medium is refused for a {hold.Bond} hold — only a medium hold has a medium to be displaced by.");
            }
            if (hold.Hold == BodyHoldKind.Grip) {
                RequirePositive(
                    errors: errors,
                    name: $"{rowPath}.grip",
                    value: hold.Grip
                );

                if (hold.Bond != BodyHoldBond.Surface) {
                    errors.Add(item: $"{rowPath}.hold 'Grip' requires bond 'Surface' — a grip pulls toward a surface normal.");
                }
            }
            if (hold.Hold == BodyHoldKind.Lift) {
                RequireRange(
                    errors: errors,
                    max: 1f,
                    min: 0f,
                    name: $"{rowPath}.lift",
                    value: hold.Lift
                );
            }
            if (
                hold.OnDrive &&
                (hold.Bond != BodyHoldBond.Surface)
            ) {
                errors.Add(item: $"{rowPath}.onDrive requires bond 'Surface' — there is no face for a drive to grab.");
            }
            if (
                (hold.Release is { Length: > 0 } release) &&
                !channelNames.Contains(item: release)
            ) {
                errors.Add(item: $"{rowPath}.release '{release}' names no declared composition channel.");
            }
            if (hold.Spend is { } spend) {
                if (
                    string.IsNullOrWhiteSpace(value: spend.State) ||
                    !stateSlots.TryGetValue(
                    key: spend.State,
                    value: out var slot
                )
                ) {
                    errors.Add(item: $"{rowPath}.spend.state '{spend.State}' names no declared body or identity state slot.");
                } else if (slot.Kind != ActionStateKind.Counter) {
                    errors.Add(item: $"{rowPath}.spend.state '{spend.State}' is a {slot.Kind} slot — a hold spends against a Counter.");
                }

                RequirePositive(
                    errors: errors,
                    name: $"{rowPath}.spend.ratePerSecond",
                    value: spend.RatePerSecond
                );
            }
        }
    }
    // The world motion defaults: positive speeds and correction smoothing distance.
    private static void ValidateMotionDefaults(in WorldMotionDefaults motion, string path, List<string> errors) {
        RequirePositive(
            value: motion.MoveSpeed,
            name: $"{path}.moveSpeed",
            errors: errors
        );
        RequirePositive(
            value: motion.TurnSpeed,
            name: $"{path}.turnSpeed",
            errors: errors
        );
        RequirePositive(
            value: motion.MaxSmoothError,
            name: $"{path}.maxSmoothError",
            errors: errors
        );
    }
    // The kit.motion gate: required (a kit with no declared row is a dead kit), coherent with its body motion
    // program's selected operations (program is null when ValidateKits already refused bodyMotionProgram, in which
    // case coherence has nothing sound to check against), and its own fields valid.
    private static void ValidateMotionRow(WorldMotion? motion, CompiledBodyMotionProgram? program, string path, ISet<string> channelNames, ISet<string> dynamicsNames, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, bool hasMedium, int simulationRateHz, List<string> errors) {
        if (motion is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (
            (program is not null) &&
            !TryValidateProgramCoherence(
            motion: motion,
            program: program,
            reason: out var reason
        )
        ) {
            errors.Add(item: $"{path} {reason}");
        }

        ValidateMotion(
            channelNames: channelNames,
            dynamicsNames: dynamicsNames,
            errors: errors,
            path: path,
            hasMedium: hasMedium,
            // The exactly-one planar-shaping rule binds the kit whose program actually shapes planar
            // velocity. A drive kit shapes it through its own row instead, so requiring a dead response
            // table there would author feel nothing reads. An unresolved program name is already refused
            // elsewhere; requiring the shaping keeps that kit's refusal complete.
            shapesPlanarVelocity: ((program is null) || program.Contains(operation: BodyMotionOp.ShapePlanarVelocity)),
            simulationRateHz: simulationRateHz,
            stateSlots: stateSlots,
            tuning: motion
        );
    }
    private static void ValidateProducerParameters(IReadOnlyDictionary<string, BodyProgramParameters> producers, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, IReadOnlyDictionary<string, BodyMotionProgram> programRows, ISet<string> channelNames, string path, List<string> errors) {
        if (producers is null) {
            errors.Add(item: $"{path} is required.");
            return;
        }

        foreach (var (name, parameters) in producers) {
            var itemPath = $"{path}[{name}]";

            if (
                string.IsNullOrWhiteSpace(value: name) ||
                !programs.TryGetValue(
                key: name,
                value: out var program
            )
            ) {
                errors.Add(item: $"{itemPath} names no program.");
                continue;
            }
            if (program.Kind != BodyProgramKind.Producer) {
                errors.Add(item: $"{itemPath} names a {program.Kind} program, not Producer.");
                continue;
            }
            if (parameters is null) {
                errors.Add(item: $"{itemPath} is required.");
                continue;
            }
            if (
                (parameters.Scalars is null) ||
                (parameters.Channels is null)
            ) {
                errors.Add(item: $"{itemPath}.scalars and {itemPath}.channels are required.");
                continue;
            }

            var required = new HashSet<string>(comparer: StringComparer.Ordinal);

            if (program.Contains(operation: BodyMotionOp.ProduceWanderIntent)) {
                required.UnionWith(other: WanderScalars);
            }
            var target = (programRows.TryGetValue(
                key: name,
                value: out var programRow
            )
                ? programRow.Target
                : null
            );

            if (program.Contains(operation: BodyMotionOp.ProduceAttendIntent)) {
                required.UnionWith(other: AttendScalars);
                if (target is BodyTargetSource.Sensed) {
                    required.Add(item: "releaseRadius");
                }
            }
            // An op's parameter set is its full runtime read set, kit-independent (the WanderScalars precedent):
            // WorldBody.FaceSensorTarget reads both scalars on every fire, and Scalar's dictionary read throws on a
            // missing name — a producer this validator admitted without them would crash the sim on first target.
            if (program.Contains(operation: BodyMotionOp.FaceSensorTarget)) {
                required.Add(item: "inwardGain");
                required.Add(item: "turnScale");
            }

            foreach (var scalar in required) {
                if (!parameters.Scalars.ContainsKey(key: scalar)) {
                    errors.Add(item: $"{itemPath}.scalars is missing instruction parameter '{scalar}'.");
                }
            }
            foreach (var (scalar, value) in parameters.Scalars) {
                if (!required.Contains(item: scalar)) {
                    errors.Add(item: $"{itemPath}.scalars contains unknown instruction parameter '{scalar}'.");
                } else {
                    RequireFinite(
                        errors: errors,
                        name: $"{itemPath}.scalars[{scalar}]",
                        value: value
                    );
                }
            }
            foreach (var (argument, channel) in parameters.Channels) {
                if (
                    !string.Equals(
                    a: argument,
                    b: "press",
                    comparisonType: StringComparison.Ordinal
                ) ||
                    !program.Contains(operation: BodyMotionOp.ProduceWanderIntent)
                ) {
                    errors.Add(item: $"{itemPath}.channels contains unknown instruction parameter '{argument}'.");
                } else if (
                    string.IsNullOrWhiteSpace(value: channel) ||
                    !channelNames.Contains(item: channel)
                ) {
                    errors.Add(item: $"{itemPath}.channels[{argument}] '{channel}' names no channel.");
                }
            }

            if (program.Contains(operation: BodyMotionOp.ProduceWanderIntent)) {
                RequirePositiveScalar(
                    errors: errors,
                    name: "softRadius",
                    parameters: parameters,
                    path: itemPath
                );
                RequirePositiveScalar(
                    errors: errors,
                    name: "turnScale",
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: "weaveFrequencyBase",
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: "weaveFrequencyRange",
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: "activityRateBase",
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: "activityRateRange",
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: "pressThreshold",
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: "altitudeRange",
                    parameters: parameters,
                    path: itemPath
                );
            }
            if (
                (target is BodyTargetSource.Sensed sensed) &&
                TryScalar(
                name: "releaseRadius",
                parameters: parameters,
                value: out var release
            ) &&
                TryScalar(
                name: "standoffRadius",
                parameters: parameters,
                value: out var standoff
            )
            ) {
                RequirePositive(
                    errors: errors,
                    name: $"{itemPath}.scalars[releaseRadius]",
                    value: release
                );
                RequirePositive(
                    errors: errors,
                    name: $"{itemPath}.scalars[standoffRadius]",
                    value: standoff
                );
                if (!((release > sensed.Range) && (sensed.Range >= standoff))) {
                    errors.Add(item: $"{itemPath} radii must satisfy releaseRadius > the target source range >= standoffRadius.");
                }
            }
            if (program.Contains(operation: BodyMotionOp.ProduceAttendIntent)) {
                RequirePositiveScalar(
                    errors: errors,
                    name: "standoffRadius",
                    parameters: parameters,
                    path: itemPath
                );
                if (TryScalar(
                    name: "approach",
                    parameters: parameters,
                    value: out var approach
                )) {
                    RequireUnitInterval(
                        errors: errors,
                        name: $"{itemPath}.scalars[approach]",
                        value: approach
                    );
                }
                if (TryScalar(
                    name: "orbit",
                    parameters: parameters,
                    value: out var orbit
                )) {
                    RequireUnitInterval(
                        errors: errors,
                        name: $"{itemPath}.scalars[orbit]",
                        value: orbit
                    );
                }
            }
        }
    }
    private static void ValidateNavigatedProducerMobility(WorldDefinition definition, WorldKit kit, CompiledBodyMotionProgram? motionProgram, IReadOnlyDictionary<string, BodyMotionProgram> programRows, string path, List<string> errors) {
        foreach (var (name, parameters) in kit.Producers) {
            if (
                parameters is null ||
                parameters.Scalars is null ||
                !programRows.TryGetValue(key: name, value: out var producerProgram) ||
                producerProgram.Target is not BodyTargetSource.Navigated navigated ||
                definition.Navigation.Rows.FirstOrDefault(predicate: domain => string.Equals(a: domain.Name, b: navigated.Domain, comparisonType: StringComparison.Ordinal)) is not { } domain
            ) {
                continue;
            }

            var producerPath = $"{path}[{name}]";
            RequirePositiveScalar(errors: errors, name: "approach", parameters: parameters, path: producerPath);
            RequirePositiveScalar(errors: errors, name: "standoffRadius", parameters: parameters, path: producerPath);
            if (
                TryScalar(name: "standoffRadius", parameters: parameters, value: out var standoff) &&
                float.IsFinite(f: standoff) &&
                standoff > domain.ArrivalDistance
            ) {
                errors.Add(item: $"{producerPath}.scalars[standoffRadius] ({standoff}) cannot exceed navigation domain '{domain.Name}' arrivalDistance ({domain.ArrivalDistance}); otherwise the producer stops before advancing its waypoint.");
            }

            if (domain.Kind == WorldNavigationKind.Surface) {
                if (motionProgram is not null && !motionProgram.RequiresRole(role: ChannelRole.MoveAdvance)) {
                    errors.Add(item: $"{producerPath} targets surface navigation domain '{domain.Name}', but kit '{kit.Name}' bodyMotionProgram consumes no MoveAdvance role.");
                }
                continue;
            }

            RequirePositiveScalar(errors: errors, name: "altitudeGain", parameters: parameters, path: producerPath);
            var directVertical = motionProgram is not null && (
                motionProgram.Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity) ||
                motionProgram.Contains(operation: BodyMotionOp.ApplyVerticalDrive)
            );
            var mediumVertical = domain.Kind == WorldNavigationKind.Medium && motionProgram?.Contains(operation: BodyMotionOp.ApplyHold) == true && (kit.Motion.Holds?.Any(predicate: hold => hold.Bond == BodyHoldBond.Medium) ?? false);
            if (!directVertical && !mediumVertical) {
                errors.Add(item: $"{producerPath} targets {domain.Kind.ToString().ToLowerInvariant()} navigation domain '{domain.Name}', but kit '{kit.Name}' bodyMotionProgram has no compatible vertical consumer (ComputeLocalTargetVelocity, ApplyVerticalDrive, or a medium ApplyHold).");
            }
            if (!definition.Channels.Any(predicate: channel => channel.Role == ChannelRole.MoveUp)) {
                errors.Add(item: $"{producerPath} targets {domain.Kind.ToString().ToLowerInvariant()} navigation domain '{domain.Name}', but the world declares no MoveUp channel.");
            }
        }
    }
    // A kit's planar shaping: exactly one of the authored velocity-response table or a named dynamics-row
    // second-order follower — never both, never neither. A dynamics row that resolves needs the world's own
    // simulation rate to compile its step-width coefficients, so a rate-0 (resident, non-stepping) world refuses a
    // kit naming one by the same door a dangling name refuses through.
    private static void ValidatePlanarShaping(IReadOnlyList<MotionResponse>? response, string? dynamics, string path, ISet<string> dynamicsNames, int simulationRateHz, List<string> errors) {
        if (dynamics is { Length: 0 }) {
            errors.Add(item: $"{path}.dynamics is empty — name a dynamics row or omit it.");
        }

        var hasResponse = (response is not null);
        var hasDynamics = (dynamics is { Length: > 0 });

        if (hasResponse && hasDynamics) {
            errors.Add(item: $"{path} authors both response and dynamics '{dynamics}' — a kit shapes planar velocity through exactly one.");
        } else if (!hasResponse && !hasDynamics) {
            errors.Add(item: $"{path} requires exactly one of response or dynamics (neither is authored).");
        }

        if (hasResponse) {
            ValidateResponse(
                errors: errors,
                path: $"{path}.response",
                response: response!
            );
        }

        if (
            hasDynamics &&
            RequireDeclared(
            declaredSet: dynamicsNames,
            errors: errors,
            field: "dynamics",
            path: path,
            rowNoun: "dynamics",
            value: dynamics
        ) &&
            (simulationRateHz <= 0)
        ) {
            errors.Add(item: $"{path}.dynamics '{dynamics}' cannot compile — the world authors no simulation rate (simulation.rateHz), and a follower's coefficients are bound to one step size.");
        }
    }
    // A velocity-response table (SIM-AFFECTING): each row's engage/release rates must be positive (a zero rate never
    // converges — a stuck body, not a feel), each gate is a body-fact-only predicate (the lane-scoped action-state
    // predicates are rejected by name), and a null (always) gate before the final row makes every later row
    // unreachable.
    private static void ValidateResponse(IReadOnlyList<MotionResponse> response, string path, List<string> errors) {
        for (var index = 0; (index < response.Count); index++) {
            var row = response[index];
            var rowPath = $"{path}[{index}]";

            if (row is null) {
                errors.Add(item: $"{rowPath} is required.");

                continue;
            }

            RequirePositive(
                value: row.EngageRate,
                name: $"{rowPath}.engageRate",
                errors: errors
            );
            RequirePositive(
                value: row.ReleaseRate,
                name: $"{rowPath}.releaseRate",
                errors: errors
            );
            ValidateMotionGate(
                predicate: row.Gate,
                path: $"{rowPath}.gate",
                errors: errors
            );

            if (
                (row.Gate is null) &&
                (index < (response.Count - 1))
            ) {
                errors.Add(item: $"{rowPath}.gate is the always-row (null) but is not last — every later row is unreachable.");
            }
        }
    }
    // Layered over ValidateEnvelopeShape: the kit's own authored value for the bounded scalar must also sit inside
    // its own declared envelope — a world that pins a scalar narrower than the baseline it authors for profileless
    // stand-ins is self-contradictory. A kit pinning its speed outright (min == max, what a kart authors) still
    // authors a moveSpeed inside that pin, so a live world.row.set retune past the cap refuses by name instead of
    // clamping silently.
    private static void ValidateScalarEnvelope(MotionScalarEnvelope envelope, float ownValue, string ownValueName, string path, List<string> errors) {
        if (!ValidateEnvelopeShape(
            envelope: envelope,
            errors: errors,
            path: path
        )) {
            return;
        }

        if (
            (ownValue < envelope.Min) ||
            (ownValue > envelope.Max)
        ) {
            errors.Add(item: $"{path} [{envelope.Min}, {envelope.Max}] does not contain the kit's own {ownValueName} ({ownValue}).");
        }
    }
    // A kit's drive row: every convergence rate positive, the steering authority curve well-formed, and a declared
    // drift naming a channel that resolves (a misspelled name is otherwise a silent, permanent no-op). The forward
    // speed, the steering rate, and the gravity trio are the motion row's own fields and are validated with it.
    private static void ValidateDriveMotion(WorldDrive tuning, string path, ISet<string> channelNames, List<string> errors) {
        RequireNonNegative(
            value: tuning.ReverseSpeed,
            name: $"{path}.reverseSpeed",
            errors: errors
        );
        RequirePositive(
            value: tuning.Accel,
            name: $"{path}.accel",
            errors: errors
        );
        RequirePositive(
            value: tuning.Brake,
            name: $"{path}.brake",
            errors: errors
        );
        RequirePositive(
            value: tuning.Coast,
            name: $"{path}.coast",
            errors: errors
        );
        RequirePositive(
            value: tuning.Grip,
            name: $"{path}.grip",
            errors: errors
        );
        RequirePositive(
            value: tuning.SteerReferenceSpeed,
            name: $"{path}.steerReferenceSpeed",
            errors: errors
        );
        RequireNonNegative(
            value: tuning.PitchRate,
            name: $"{path}.pitchRate",
            errors: errors
        );

        if (
            !float.IsFinite(f: tuning.SteerFalloff) ||
            (tuning.SteerFalloff < 0f) ||
            (tuning.SteerFalloff > 1f)
        ) {
            errors.Add(item: $"{path}.steerFalloff {tuning.SteerFalloff} must be within [0, 1].");
        }

        if (tuning.Drift is not { } drift) {
            return;
        }

        if (
            (drift.Channel is not { Length: > 0 } channel) ||
            !channelNames.Contains(item: channel)
        ) {
            errors.Add(item: $"{path}.drift.channel '{drift.Channel}' names no declared composition channel.");
        }

        RequirePositive(
            value: drift.Grip,
            name: $"{path}.drift.grip",
            errors: errors
        );
        RequirePositive(
            value: drift.SteerScale,
            name: $"{path}.drift.steerScale",
            errors: errors
        );
    }
    private static readonly string[] WanderScalars = [
        "forward", "softRadius", "weaveAmplitude", "inwardGain", "turnScale",
        "weaveFrequencyBase", "weaveFrequencyRange", "altitudeGain", "activityRateBase", "activityRateRange",
        "strafeWave", "turnWave", "upWave", "pitchWave", "rollTurn", "pressThreshold", "altitudeBase", "altitudeRange",
    ];
    private static readonly string[] AttendScalars = ["standoffRadius", "approach", "orbit", "altitudeGain"];

    /// <summary>The tuning facets a body motion program's selected operations read from a kit's declared
    /// <see cref="WorldMotion"/> row — the validator's own mapping (never convention; see
    /// <see cref="RequiredMotionTuningFacets"/>/<see cref="SuppliedMotionTuningFacets"/>) that a new operation must
    /// extend. A declared row missing a facet an operation still reads refuses by name at validation instead of the
    /// operation reading a silent zero at runtime.</summary>
    [Flags]
    private enum MotionTuningFacet : ushort {
        None = 0,

        /// <summary>MoveSpeed/TurnSpeed — read unconditionally by every Motion-kind body motion program.</summary>
        Speed = 1,

        /// <summary>RiseGravity/FallGravity/MaxFallSpeed, the full gravity arc (<see cref="BodyMotionOp.ApplyVerticalGravity"/>) —
        /// the same op <see cref="CompiledBodyMotionProgram.OwnsVerticalContactState"/> keys off, at runtime, to decide
        /// whether contact resolution may write back into a body's vertical channel.</summary>
        GravityArc = 2,

        /// <summary>RiseGravity alone, read as a symmetric bleed rate (<see cref="BodyMotionOp.ApplyVerticalDecay"/>).</summary>
        GravityBleed = 4,

        /// <summary>The response table OR the dynamics follower (<see cref="BodyMotionOp.ShapePlanarVelocity"/>) —
        /// whichever the exactly-one authoring rule admitted.</summary>
        PlanarResponse = 8,

        /// <summary>SprintMultiplier/SprintChannel (<see cref="BodyMotionOp.ComputePlanarTargetVelocity"/>).</summary>
        Sprint = 16,

        /// <summary>MoveFrame/FacingSnap (<see cref="BodyMotionOp.ResolveYawAttitudeAndPlanarFrame"/>/
        /// <see cref="BodyMotionOp.SnapYawToPlanarIntent"/>).</summary>
        WorldFrame = 32,

        /// <summary>The optional <c>drive</c> row — longitudinal accel/brake/coast, lateral grip/drift, and
        /// speed-scaled steering (<see cref="BodyMotionOp.ResolveDriveFrame"/>/
        /// <see cref="BodyMotionOp.ShapeDriveVelocity"/>). Supplied only by a kit that authors one.</summary>
        Drive = 64,

        /// <summary>The ordered hold list (<see cref="BodyMotionOp.ResolveHold"/>/
        /// <see cref="BodyMotionOp.ApplyHold"/>), plus the gravity arc a hold's own gravity and lift laws
        /// integrate.</summary>
        Holds = 512,
    }
}
