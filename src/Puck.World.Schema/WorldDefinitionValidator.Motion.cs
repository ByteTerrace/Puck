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
                BodyMotionOp.ResolveVehicleFrame or BodyMotionOp.ShapeVehicleVelocity => MotionTuningFacet.VehicleDrive,
                BodyMotionOp.ResolveHold or BodyMotionOp.ApplyHold => (MotionTuningFacet.GravityArc | MotionTuningFacet.Holds),
                _ => MotionTuningFacet.None,
            };
        }

        return facets;
    }
    // The model→facet mapping: what each WorldMotionModel arm supplies. A new arm is a localized addition here,
    // alongside its record arm (WorldDefinition.cs), its WorldBody integrator, and any new BodyMotionOp cases
    // RequiredMotionTuningFacets needs — never a hunt. Grounded supplies every facet defined
    // today because it is, today, also the only arm the world's "free" body motion program authors (see
    // WorldMotionModel.Grounded's remarks) — a strict superset of what free's operations read.
    private static MotionTuningFacet SuppliedMotionTuningFacets(WorldMotionModel model) => model switch {
        WorldMotionModel.Grounded => (MotionTuningFacet.Speed | MotionTuningFacet.GravityArc | MotionTuningFacet.GravityBleed
            | MotionTuningFacet.PlanarResponse | MotionTuningFacet.Sprint | MotionTuningFacet.WorldFrame | MotionTuningFacet.Holds),
        // The vehicle arm carries its own gravity trio (contact-pinned variants run ApplyVerticalGravity; flying
        // variants bleed impulses through ApplyVerticalDecay) but none of grounded's planar-shaping facets — pairing
        // a vehicle model with ShapePlanarVelocity/ComputePlanarTargetVelocity/the yaw-frame ops refuses by name.
        WorldMotionModel.Vehicle => (MotionTuningFacet.Speed | MotionTuningFacet.GravityArc | MotionTuningFacet.GravityBleed
            | MotionTuningFacet.VehicleDrive),
        _ => MotionTuningFacet.None,
    };
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
    private static Dictionary<string, CompiledBodyMotionProgram> ValidateBodyMotionPrograms(IReadOnlyList<BodyMotionProgram> programs, ISet<string> targetRegisterNames, ISet<string> curveNames, int simulationRateHz, List<string> errors) {
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
    // The shape every authored envelope must have regardless of arm: min/max finite, min <= max (FixedQ4816.Clamp's
    // own precondition, refused here so it never throws at seat-resolve time), min non-negative — every consumer
    // bounds a speed magnitude, and reverse travel is its own positive scalar (reverseTopSpeed), so a negative
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
    // A kit's full grounded locomotion tuning: speeds, gravity, and the velocity-response table every body
    // integrates under.
    private static void ValidateGroundedMotion(WorldMotionModel.Grounded tuning, string path, ISet<string> channelNames, ISet<string> dynamicsNames, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, bool hasMedium, int simulationRateHz, List<string> errors) {
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
        ValidatePlanarShaping(
            dynamics: tuning.Dynamics,
            dynamicsNames: dynamicsNames,
            errors: errors,
            path: path,
            response: tuning.Response,
            simulationRateHz: simulationRateHz
        );

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

        // Absent, this envelope is wide-open (unclamped). Another arm's own overridable scalar walks the same gate.
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
    // The kit.motion gate: required (a kit with no declared model is a dead kit), coherent with its body motion
    // program's selected operations (program is null when ValidateKits already refused bodyMotionProgram, in which
    // case coherence has nothing sound to check against), and per-arm valid. A new arm is a new case below.
    private static void ValidateMotionModel(WorldMotionModel? model, CompiledBodyMotionProgram? program, string path, ISet<string> channelNames, ISet<string> dynamicsNames, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, bool hasMedium, int simulationRateHz, List<string> errors) {
        if (model is null) {
            errors.Add(item: $"{path} is required.");

            return;
        }

        if (
            (program is not null) &&
            !TryValidateProgramCoherence(
            model: model,
            program: program,
            reason: out var reason
        )
        ) {
            errors.Add(item: $"{path} {reason}");
        }

        switch (model) {
            case WorldMotionModel.Grounded grounded:
                ValidateGroundedMotion(
                    channelNames: channelNames,
                    dynamicsNames: dynamicsNames,
                    errors: errors,
                    path: path,
                    hasMedium: hasMedium,
                    simulationRateHz: simulationRateHz,
                    stateSlots: stateSlots,
                    tuning: grounded
                );

                break;
            case WorldMotionModel.Vehicle vehicle:
                ValidateVehicleMotion(
                    channelNames: channelNames,
                    errors: errors,
                    path: path,
                    tuning: vehicle
                );

                break;
            default:
                errors.Add(item: $"{path} is an unknown motion model kind '{model.GetType().Name}'.");

                break;
        }
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
    // stand-ins is self-contradictory. Meaningful only where the bounded value is a fallback a separate, unvalidated
    // live read (a seated profile) can diverge from — see ValidateVehicleMotion's remarks for the arm that
    // deliberately skips this layer.
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
    private static void ValidateVehicleMotion(WorldMotionModel.Vehicle tuning, string path, ISet<string> channelNames, List<string> errors) {
        RequirePositive(
            value: tuning.TopSpeed,
            name: $"{path}.topSpeed",
            errors: errors
        );
        RequireNonNegative(
            value: tuning.ReverseTopSpeed,
            name: $"{path}.reverseTopSpeed",
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
            value: tuning.CoastDrag,
            name: $"{path}.coastDrag",
            errors: errors
        );
        RequirePositive(
            value: tuning.Grip,
            name: $"{path}.grip",
            errors: errors
        );
        RequirePositive(
            value: tuning.SteerRate,
            name: $"{path}.steerRate",
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
            value: tuning.DriftSteerScale,
            name: $"{path}.driftSteerScale",
            errors: errors
        );
        RequirePositive(
            value: tuning.BoostMultiplier,
            name: $"{path}.boostMultiplier",
            errors: errors
        );

        if (
            !float.IsFinite(f: tuning.SteerFalloff) ||
            (tuning.SteerFalloff < 0f) ||
            (tuning.SteerFalloff > 1f)
        ) {
            errors.Add(item: $"{path}.steerFalloff {tuning.SteerFalloff} must be within [0, 1].");
        }

        if (tuning.DriftChannel is { Length: > 0 } driftChannel) {
            if (!channelNames.Contains(item: driftChannel)) {
                errors.Add(item: $"{path}.driftChannel '{driftChannel}' names no declared composition channel.");
            }

            if (
                !float.IsFinite(f: tuning.DriftGrip) ||
                (tuning.DriftGrip <= 0f)
            ) {
                errors.Add(item: $"{path}.driftGrip {tuning.DriftGrip} must be positive when a drift channel is declared.");
            }
        }

        if (
            (tuning.BoostChannel is { Length: > 0 } boostChannel) &&
            !channelNames.Contains(item: boostChannel)
        ) {
            errors.Add(item: $"{path}.boostChannel '{boostChannel}' names no declared composition channel.");
        }

        // Well-formedness only (finite min/max, min <= max) — deliberately NOT the own-value-in-range check
        // ValidateScalarEnvelope also applies to grounded's moveSpeed. Grounded's baseline is a profileless fallback
        // the live-clamped read (Profile's own speed) can diverge from, so an own-value check keeps that fallback
        // sane. The vehicle arm has no such second channel: topSpeed itself IS the live-clamped read (world.row.set
        // retunes it in place), so requiring it inside its own envelope would refuse the exact retune-past-the-cap
        // this envelope exists to catch. A malformed envelope (min > max, non-finite) still refuses.
        if (tuning.TopSpeedEnvelope is { } topSpeedEnvelope) {
            ValidateEnvelopeShape(
                envelope: topSpeedEnvelope,
                errors: errors,
                path: $"{path}.topSpeedEnvelope"
            );
        }
    }

    private static readonly string[] WanderScalars = [
        "forward", "softRadius", "weaveAmplitude", "inwardGain", "turnScale",
        "weaveFrequencyBase", "weaveFrequencyRange", "altitudeGain", "activityRateBase", "activityRateRange",
        "strafeWave", "turnWave", "upWave", "pitchWave", "rollTurn", "pressThreshold", "altitudeBase", "altitudeRange",
    ];
    private static readonly string[] AttendScalars = ["standoffRadius", "approach", "orbit", "altitudeGain"];

    /// <summary>The tuning facets a body motion program's selected operations read from a kit's declared
    /// <see cref="WorldMotionModel"/> — the validator's own mapping (never convention; see
    /// <see cref="RequiredMotionTuningFacets"/>/<see cref="SuppliedMotionTuningFacets"/>) that a new operation or a new
    /// model arm must extend. A declared model missing a facet an operation still reads refuses by name at
    /// validation instead of the operation reading a silent zero at runtime.</summary>
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
        /// whichever the arm's exactly-one authoring rule admitted.</summary>
        PlanarResponse = 8,

        /// <summary>SprintMultiplier/SprintChannel (<see cref="BodyMotionOp.ComputePlanarTargetVelocity"/>).</summary>
        Sprint = 16,

        /// <summary>MoveFrame/FacingSnap (<see cref="BodyMotionOp.ResolveYawAttitudeAndPlanarFrame"/>/
        /// <see cref="BodyMotionOp.SnapYawToPlanarIntent"/>).</summary>
        WorldFrame = 32,

        /// <summary>The anisotropic drive family — longitudinal accel/brake/coast, lateral grip/drift, and
        /// speed-scaled steering (<see cref="BodyMotionOp.ResolveVehicleFrame"/>/
        /// <see cref="BodyMotionOp.ShapeVehicleVelocity"/>).</summary>
        VehicleDrive = 64,

        /// <summary>The ordered hold list (<see cref="BodyMotionOp.ResolveHold"/>/
        /// <see cref="BodyMotionOp.ApplyHold"/>), plus the gravity arc a hold's own gravity and lift laws
        /// integrate.</summary>
        Holds = 512,
    }
}
