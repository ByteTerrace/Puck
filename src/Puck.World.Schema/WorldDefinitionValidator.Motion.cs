using Puck.Physics.Motion;
using Puck.Maths;

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
            RequirePositiveFixed(
                errors: errors,
                name: $"{path}.scalars[{name}]",
                value: value
            );
        }
    }
    // A positive authored scalar that rounds to raw zero is not positive in the simulation. Keep that refusal beside
    // the float-domain sign check so every caller still receives a document refusal before fixed compilation.
    private static void RequirePositiveFixed(float value, string name, List<string> errors) {
        RequirePositive(
            errors: errors,
            name: name,
            value: value
        );

        if (float.IsFinite(f: value) && (value > 0f) && (FixedQ4816.FromDouble(value: value) <= FixedQ4816.Zero)) {
            errors.Add(item: $"{name} {value} is positive but quantizes to zero in Q48.16.");
        }
    }
    // The non-negative sibling: zero is an admitted authored value, but a positive one must still survive Q48.16.
    private static void RequireNonNegativeFixed(float value, string name, List<string> errors) {
        RequireNonNegative(
            errors: errors,
            name: name,
            value: value
        );

        if (float.IsFinite(f: value) && (value > 0f) && (FixedQ4816.FromDouble(value: value) <= FixedQ4816.Zero)) {
            errors.Add(item: $"{name} {value} is positive but quantizes to zero in Q48.16.");
        }
    }
    // The op→facet mapping: walks a COMPILED program's selected operations (never the authored list — compilation
    // already rejected an unknown/inadmissible opcode) and unions the facets each one reads. Speed/Turn are
    // structurally mandatory fields of every WorldMotion row (unconditional for every Motion-kind program) —
    // callers only reach here once the kit's program has already been confirmed Motion-kind (see ValidateKits).
    private static MotionTuningFacet RequiredMotionTuningFacets(CompiledBodyMotionProgram program) {
        var facets = MotionTuningFacet.Speed;

        foreach (var op in Enum.GetValues<BodyMotionOp>()) {
            if (!program.Contains(operation: op)) {
                continue;
            }

            facets |= op switch {
                BodyMotionOp.ShapeVelocity => MotionTuningFacet.Shaping,
                BodyMotionOp.ResolveHold or BodyMotionOp.ApplyHold => MotionTuningFacet.Holds,
                _ => MotionTuningFacet.None,
            };
        }

        return facets;
    }
    // What a kit's motion row supplies. Speed/Turn are always supplied (structurally mandatory record fields); the
    // two OPTIONAL rows — Holds and Shaping — each refuse their own program by facet name when the kit authors none.
    private static MotionTuningFacet SuppliedMotionTuningFacets(WorldMotion motion) => (MotionTuningFacet.Speed
        | ((motion.Holds is { Count: > 0 })
        ? MotionTuningFacet.Holds
        : MotionTuningFacet.None)
        | ((motion.Shaping is { Count: > 0 })
        ? MotionTuningFacet.Shaping
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
                    var flocks = compiledProgram.Contains(operation: BodyMotionOp.ProduceFlockIntent);
                    if (flocks && (compiledProgram.Contains(BodyMotionOp.ProduceSteeringIntent) || compiledProgram.Contains(BodyMotionOp.FaceSensorTarget))) {
                        errors.Add($"{path} ProduceFlockIntent owns the movement preference and cannot combine with another intent or facing producer.");
                    }

                    // ProduceSteeringIntent's own runtime shape is roam-only unless this program also senses a
                    // target (SenseNearestInCone) — no separate opcode-pairing rule needed; a bare
                    // ProduceSteeringIntent is a legitimate roam-only producer.
                    if (
                        compiledProgram.Contains(operation: BodyMotionOp.FaceSensorTarget) &&
                        !senses
                    ) {
                        errors.Add(item: $"{path} producer opcode '{BodyMotionOp.FaceSensorTarget}' requires '{BodyMotionOp.SenseNearestInCone}'.");
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
                            RequireRange(
                                value: sensed.Range,
                                min: 1f / 65536f,
                                max: 1_000_000f,
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
    // A kit's full locomotion tuning: speed, turn, holds, and the shaping table every body integrates under.
    private static void ValidateMotion(WorldMotion tuning, string path, ISet<string> channelNames, ISet<string> dynamicsNames, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, bool hasMedium, bool hasMoveUpChannel, int simulationRateHz, List<string> errors) {
        if (tuning.Speed is null) {
            errors.Add(item: $"{path}.speed is required.");
        } else {
            ValidateSpeed(
                channelNames: channelNames,
                errors: errors,
                path: $"{path}.speed",
                speed: tuning.Speed
            );
        }

        if (tuning.Turn is null) {
            errors.Add(item: $"{path}.turn is required.");
        } else {
            ValidateTurn(
                errors: errors,
                path: $"{path}.turn",
                turn: tuning.Turn
            );
        }

        ValidateShaping(
            channelNames: channelNames,
            dynamicsNames: dynamicsNames,
            errors: errors,
            path: $"{path}.shaping",
            shaping: tuning.Shaping,
            simulationRateHz: simulationRateHz
        );

        if (!Enum.IsDefined(value: tuning.MoveFrame)) {
            errors.Add(item: $"{path}.moveFrame '{tuning.MoveFrame}' is not a defined MotionMoveFrame.");
        }

        ValidateHolds(
            channelNames: channelNames,
            errors: errors,
            hasMedium: hasMedium,
            hasMoveUpChannel: hasMoveUpChannel,
            holds: tuning.Holds,
            path: $"{path}.holds",
            stateSlots: stateSlots
        );

        ValidateUpTurn(
            errors: errors,
            path: $"{path}.upTurn",
            upTurn: tuning.UpTurn
        );

        ValidateObstructionLatch(
            errors: errors,
            obstruction: tuning.Obstruction,
            path: $"{path}.obstruction"
        );

        RequirePositiveFixed(
            value: tuning.GroundStick,
            name: $"{path}.groundStick",
            errors: errors
        );
    }
    // A kit's movement rate: a positive fallback speed, its optional seat-time envelope (absent is wide-open), and
    // its optional held multiplier (a misspelled channel name is otherwise a silent, permanent no-op).
    private static void ValidateSpeed(WorldSpeed speed, string path, ISet<string> channelNames, List<string> errors) {
        RequirePositiveFixed(
            value: speed.Value,
            name: $"{path}.value",
            errors: errors
        );

        if (speed.Envelope is { } envelope) {
            ValidateScalarEnvelope(
                envelope: envelope,
                ownValue: speed.Value,
                ownValueName: "value",
                path: $"{path}.envelope",
                errors: errors
            );
        }

        if (speed.Held is { } held) {
            if (
                string.IsNullOrWhiteSpace(value: held.Channel) ||
                !channelNames.Contains(item: held.Channel)
            ) {
                errors.Add(item: $"{path}.held.channel '{held.Channel}' names no declared composition channel.");
            }

            RequirePositiveFixed(
                value: held.Multiplier,
                name: $"{path}.held.multiplier",
                errors: errors
            );
        }
    }
    // A kit's steering rate: a positive rate at full authority, an optional speed-scaled authority curve (a positive
    // reference speed and a falloff fraction in [0, 1]), and a non-negative pitch rate for the flying drive variant.
    private static void ValidateTurn(WorldTurn turn, string path, List<string> errors) {
        RequirePositiveFixed(
            value: turn.Rate,
            name: $"{path}.rate",
            errors: errors
        );
        RequireNonNegative(
            value: turn.PitchRate,
            name: $"{path}.pitchRate",
            errors: errors
        );

        if (turn.ReferenceSpeed is { } referenceSpeed) {
            RequirePositiveFixed(
                value: referenceSpeed,
                name: $"{path}.referenceSpeed",
                errors: errors
            );
        }

        if (
            !float.IsFinite(f: turn.Falloff) ||
            (turn.Falloff < 0f) ||
            (turn.Falloff > 1f)
        ) {
            errors.Add(item: $"{path}.falloff {turn.Falloff} must be within [0, 1].");
        }

        // Strictly inside a right angle: the drive frame is built by rotating the flat frame's forward through this
        // scalar, and a clamp at or past pi/2 would let it reach or pass vertical, where the yaw frame it derives
        // from inverts.
        if (
            !float.IsFinite(f: turn.MaxPitch) ||
            (turn.MaxPitch <= 0f) ||
            (turn.MaxPitch >= (float)(Math.PI / 2.0))
        ) {
            errors.Add(item: $"{path}.maxPitch {turn.MaxPitch} must be within (0, pi/2).");
        } else if (FixedQ4816.FromDouble(value: turn.MaxPitch) <= FixedQ4816.Zero) {
            errors.Add(item: $"{path}.maxPitch {turn.MaxPitch} is positive but quantizes to zero in Q48.16.");
        }
    }
    // A kit's up-axis steering ceilings: both positive, finite half-angle rates.
    private static void ValidateUpTurn(WorldUpTurnRates upTurn, string path, List<string> errors) {
        RequirePositiveFixed(
            value: upTurn.Field,
            name: $"{path}.field",
            errors: errors
        );
        RequirePositiveFixed(
            value: upTurn.Contact,
            name: $"{path}.contact",
            errors: errors
        );
    }
    // A kit's obstruction-witness latch: a positive displacement and grace window, a non-negative idle threshold.
    private static void ValidateObstructionLatch(WorldObstructionLatch obstruction, string path, List<string> errors) {
        RequirePositiveFixed(
            value: obstruction.Displacement,
            name: $"{path}.displacement",
            errors: errors
        );
        RequireNonNegative(
            value: obstruction.IdleThreshold,
            name: $"{path}.idleThreshold",
            errors: errors
        );
        if (obstruction.Displacement > 0f && float.IsFinite(f: obstruction.Displacement) && FixedQ4816.FromDouble(value: ((double)obstruction.Displacement * obstruction.Displacement)) <= FixedQ4816.Zero) {
            errors.Add(item: $"{path}.displacement {obstruction.Displacement} is positive but its squared Q48.16 comparison threshold quantizes to zero.");
        }
        if (obstruction.GraceSeconds <= 0m) {
            errors.Add(item: $"{path}.graceSeconds {obstruction.GraceSeconds} must be positive.");
        } else if (!FixedTickConversion.TryDurationEngineTicksExact(
            seconds: obstruction.GraceSeconds,
            ticks: out var graceTicks
        ) || (graceTicks == 0UL)) {
            errors.Add(item: $"{path}.graceSeconds {obstruction.GraceSeconds} does not convert to a positive exact whole tick across the {FixedTickConversion.TicksPerSecond} engine-tick bridge.");
        }
    }
    // A row nothing can ever make ineligible: bonded to no surface at all (Free), authoring neither a release
    // channel nor a spend. ResolveHold always takes such a row once every earlier candidate is ineligible, so a hold
    // list authoring one guarantees ApplyHold always has a current hold to read. A Medium row is NOT unconditional —
    // ResolveHold takes it only when the world's own lattice offers a medium column where the body stands
    // (WorldBody.Hold.cs's ResolveHold: `if (m_mediumSurface is not null) { chosen = index; } else { continue; }`),
    // so a body outside its medium is exactly the case a Medium-only hold list leaves with nothing to fall to.
    private static bool HoldIsUnconditional(WorldHold hold) => ((hold is not null) && (hold.Bond == BodyHoldBond.Free) && (hold.Release is null) && (hold.Spend is null));
    // The ordered hold list: a unique name per row, a cone inside [0, 180] with min < max for a surface row (and no
    // cone at all for a free one), the kind's own operands, and every named channel/state slot resolvable. Absence,
    // and the presence of an unconditional row, are checked by the caller (ValidateMotionRow) against the compiled
    // program's kind — the hold list is the only spelling of a vertical channel, so a Motion-kind kit must always
    // author at least one row, including an unconditional one.
    private static void ValidateHolds(IReadOnlyList<WorldHold>? holds, string path, ISet<string> channelNames, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, bool hasMedium, bool hasMoveUpChannel, List<string> errors) {
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
                RequirePositiveFixed(
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

                RequirePositiveFixed(
                    errors: errors,
                    name: $"{rowPath}.reach",
                    value: hold.Reach
                );
            } else if (hold.Cone is not null) {
                errors.Add(item: $"{rowPath}.cone is refused for a {hold.Bond} hold — only a surface hold has a face for a cone to admit.");
            }
            if (hold.Bond == BodyHoldBond.Medium) {
                if (!hasMedium) {
                    errors.Add(item: $"{rowPath}.bond 'Medium' requires a medium lattice row (state.world[].field.medium) — a medium hold implies a medium to stand in.");
                }
                if (hold.Medium is not { } medium) {
                    errors.Add(item: $"{rowPath}.medium is required for a medium hold — the idle drift, the equilibrium offset and the settle rate are its whole law.");
                } else {
                    RequirePositiveFixed(
                        errors: errors,
                        name: $"{rowPath}.medium.equilibriumOffset",
                        value: medium.EquilibriumOffset
                    );
                    RequireFinite(
                        errors: errors,
                        name: $"{rowPath}.medium.idleDrift",
                        value: medium.IdleDrift
                    );
                    RequirePositiveFixed(
                        errors: errors,
                        name: $"{rowPath}.medium.settleRate",
                        value: medium.SettleRate
                    );
                }
            } else if (hold.Medium is not null) {
                errors.Add(item: $"{rowPath}.medium is refused for a {hold.Bond} hold — only a medium hold has a medium to be displaced by.");
            }
            if (
                (hold.Bond == BodyHoldBond.Medium) &&
                (hold.Hold is BodyHoldKind.Gravity or BodyHoldKind.Lift)
            ) {
                errors.Add(item: $"{rowPath}.hold '{hold.Hold}' is refused on a Medium bond — a medium displaces a body by its own law, so a medium row applies no arc.");
            }
            // ApplyHoldGravityDecay owns a full-lift row's vertical channel outright and bleeds it at Rise alone (a
            // symmetric bleed, never the asymmetric arc), so Fall and the envelope go unread there — requiring them
            // would demand fields nothing reads.
            var fullLift = ((hold.Hold == BodyHoldKind.Lift) && (hold.Lift >= 1f));

            if (
                (hold.Bond != BodyHoldBond.Medium) &&
                (hold.Hold is BodyHoldKind.Gravity or BodyHoldKind.Lift)
            ) {
                if (hold.Gravity is not { } gravity) {
                    errors.Add(item: (fullLift
                        ? $"{rowPath}.gravity is required for a full-lift hold — its Rise is the bleed rate the channel decays at."
                        : $"{rowPath}.gravity is required for a {hold.Hold} hold — the rise and fall are its whole vertical arc."
                    ));
                } else {
                    RequirePositiveFixed(
                        errors: errors,
                        name: $"{rowPath}.gravity.rise",
                        value: gravity.Rise
                    );

                    if (!fullLift) {
                        RequirePositiveFixed(
                            errors: errors,
                            name: $"{rowPath}.gravity.fall",
                            value: gravity.Fall
                        );
                    }
                }
            } else if (hold.Gravity is not null) {
                errors.Add(item: $"{rowPath}.gravity is refused for a {hold.Hold} hold on a {hold.Bond} bond — only a Gravity or Lift hold falls under an arc.");
            }
            // The vertical-channel envelope: required for a medium (both directions) and for a gravity/lift hold
            // short of full lift (sink only — the rise direction is never clamped by that arc); refused otherwise.
            var needsEnvelope = ((hold.Bond == BodyHoldBond.Medium) || ((hold.Hold is BodyHoldKind.Gravity or BodyHoldKind.Lift) && !fullLift));

            if (needsEnvelope) {
                if (hold.Envelope is not { } envelope) {
                    errors.Add(item: $"{rowPath}.envelope is required for a {((hold.Bond == BodyHoldBond.Medium) ? "medium" : hold.Hold.ToString())} hold — the terminal speed(s) it is bounded by.");
                } else {
                    RequirePositiveFixed(
                        errors: errors,
                        name: $"{rowPath}.envelope.sinkSpeed",
                        value: envelope.SinkSpeed
                    );

                    if (hold.Bond == BodyHoldBond.Medium) {
                        if (envelope.RiseSpeed is not { } riseSpeed) {
                            errors.Add(item: $"{rowPath}.envelope.riseSpeed is required for a medium hold — a medium bounds both directions.");
                        } else {
                            RequirePositiveFixed(
                                errors: errors,
                                name: $"{rowPath}.envelope.riseSpeed",
                                value: riseSpeed
                            );
                        }
                    } else if (envelope.RiseSpeed is not null) {
                        errors.Add(item: $"{rowPath}.envelope.riseSpeed is refused for a {hold.Hold} hold — its own arc never clamps a rise.");
                    }
                }
            } else if (hold.Envelope is not null) {
                errors.Add(item: $"{rowPath}.envelope is refused for a {hold.Hold} hold on a {hold.Bond} bond — nothing here reads a vertical bound.");
            }
            RequireRange(
                errors: errors,
                max: 1f,
                min: 0f,
                name: $"{rowPath}.thrust",
                value: hold.Thrust
            );
            if (
                (hold.Thrust > 0f) &&
                !hasMoveUpChannel
            ) {
                errors.Add(item: $"{rowPath}.thrust is positive but the world declares no MoveUp channel — a row's thrust reads the MoveUp role, so nothing could ever command it.");
            }
            if (hold.Hold == BodyHoldKind.Pull) {
                RequirePositiveFixed(
                    errors: errors,
                    name: $"{rowPath}.pull",
                    value: hold.Pull
                );

                if (hold.Bond != BodyHoldBond.Surface) {
                    errors.Add(item: $"{rowPath}.hold 'Pull' requires bond 'Surface' — a pull draws toward a surface normal.");
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

                RequirePositiveFixed(
                    errors: errors,
                    name: $"{rowPath}.spend.ratePerSecond",
                    value: spend.RatePerSecond
                );
            }
        }
    }
    // The world motion defaults: positive speeds and correction smoothing distance.
    private static void ValidateMotionDefaults(in WorldMotionDefaults motion, string path, List<string> errors) {
        RequirePositiveFixed(
            value: motion.MoveSpeed,
            name: $"{path}.moveSpeed",
            errors: errors
        );
        RequirePositiveFixed(
            value: motion.TurnSpeed,
            name: $"{path}.turnSpeed",
            errors: errors
        );
        RequirePositiveFixed(
            value: motion.MaxSmoothError,
            name: $"{path}.maxSmoothError",
            errors: errors
        );
    }
    // The kit.motion gate: required (a kit with no declared row is a dead kit), coherent with its body motion
    // program's selected operations (program is null when ValidateKits already refused bodyMotionProgram, in which
    // case coherence has nothing sound to check against), and its own fields valid.
    private static void ValidateMotionRow(WorldMotion? motion, CompiledBodyMotionProgram? program, string path, ISet<string> channelNames, ISet<string> dynamicsNames, IReadOnlyDictionary<string, ActionStateSlot> stateSlots, bool hasMedium, bool hasMoveUpChannel, int simulationRateHz, List<string> errors) {
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
        // The hold list is the only spelling of a vertical channel, so a Motion-kind program requires one regardless
        // of which facets its own selected operations happen to read — a kit with no vertical law of its own still
        // authors a single row of kind None rather than omitting the list.
        if (program is { Kind: BodyProgramKind.Motion }) {
            if (motion.Holds is not { Count: > 0 } holds) {
                errors.Add(item: $"{path}.holds is required for a Motion-kind body motion program ('{program.Name}') — the hold list is the only spelling of a vertical channel.");
            } else if (!holds.Any(predicate: HoldIsUnconditional)) {
                errors.Add(item: $"{path}.holds authors no unconditional row (a Free bond authoring neither release nor spend) for a Motion-kind body motion program ('{program.Name}') — ApplyHold keeps whatever vertical channel the body carried in the tick every row goes ineligible, so a hold list must always leave one row nothing can drop. A Medium row does not count: ResolveHold takes it only where the world's own lattice offers a medium column.");
            }
            if (
                program.Contains(operation: BodyMotionOp.ApplyHold) &&
                !program.Contains(operation: BodyMotionOp.ResolveHold)
            ) {
                errors.Add(item: $"{path} body motion program '{program.Name}' selects '{BodyMotionOp.ApplyHold}' without '{BodyMotionOp.ResolveHold}' — ApplyHold applies whatever row ResolveHold selected, and never runs paired with a selector of its own.");
            }
        }

        ValidateMotion(
            channelNames: channelNames,
            dynamicsNames: dynamicsNames,
            errors: errors,
            path: path,
            hasMedium: hasMedium,
            hasMoveUpChannel: hasMoveUpChannel,
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

            ValidateFlockProfile(parameters.Flock, program, itemPath, errors);

            var target = (programRows.TryGetValue(
                key: name,
                value: out var programRow
            )
                ? programRow.Target
                : null
            );
            var senses = program.Contains(operation: BodyMotionOp.SenseNearestInCone);

            // The one required-scalar derivation — CompiledBodyProducer.ResolveRequiredScalars — so this validator
            // and the compiler can never disagree about which scalars a program's selected operations and authored
            // arguments require.
            var requiredParameters = CompiledBodyProducer.ResolveRequiredScalars(
                program: program,
                target: target,
                parameters: parameters
            );
            var required = new HashSet<string>(
                collection: requiredParameters.Select(selector: BodyProducerParameterVocabulary.Name),
                comparer: StringComparer.Ordinal
            );

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
                    b: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.Press),
                    comparisonType: StringComparison.Ordinal
                ) ||
                    !program.Contains(operation: BodyMotionOp.ProduceSteeringIntent)
                ) {
                    errors.Add(item: $"{itemPath}.channels contains unknown instruction parameter '{argument}'.");
                } else if (
                    string.IsNullOrWhiteSpace(value: channel) ||
                    !channelNames.Contains(item: channel)
                ) {
                    errors.Add(item: $"{itemPath}.channels[{argument}] '{channel}' names no channel.");
                }
            }

            if (program.Contains(operation: BodyMotionOp.ProduceSteeringIntent)) {
                RequirePositiveScalar(
                    errors: errors,
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.SoftRadius),
                    parameters: parameters,
                    path: itemPath
                );
                RequirePositiveScalar(
                    errors: errors,
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.TurnScale),
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.WeaveFrequencyBase),
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.WeaveFrequencyRange),
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.ActivityRateBase),
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.ActivityRateRange),
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.PressThreshold),
                    parameters: parameters,
                    path: itemPath
                );
                RequireNonNegativeScalar(
                    errors: errors,
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.AltitudeRange),
                    parameters: parameters,
                    path: itemPath
                );
            }
            if (
                (target is BodyTargetSource.Sensed sensed) &&
                TryScalar(
                name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.ReleaseRadius),
                parameters: parameters,
                value: out var release
            ) &&
                TryScalar(
                name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.StandoffRadius),
                parameters: parameters,
                value: out var standoff
            )
            ) {
                RequirePositiveFixed(
                    errors: errors,
                    name: $"{itemPath}.scalars[{BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.ReleaseRadius)}]",
                    value: release
                );
                RequirePositiveFixed(
                    errors: errors,
                    name: $"{itemPath}.scalars[{BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.StandoffRadius)}]",
                    value: standoff
                );
                if (!((release > sensed.Range) && (sensed.Range >= standoff))) {
                    errors.Add(item: $"{itemPath} radii must satisfy releaseRadius > the target source range >= standoffRadius.");
                }
            }
            // The approach shape is reachable only once a target can be sensed — the presence check above already
            // requires standoffRadius/approach/orbit exactly when senses holds, so a bare roam producer authors
            // none of them; these value checks run under the same condition.
            if (senses && program.Contains(operation: BodyMotionOp.ProduceSteeringIntent)) {
                RequirePositiveScalar(
                    errors: errors,
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.StandoffRadius),
                    parameters: parameters,
                    path: itemPath
                );
                if (TryScalar(
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.Approach),
                    parameters: parameters,
                    value: out var approach
                )) {
                    RequireUnitInterval(
                        errors: errors,
                        name: $"{itemPath}.scalars[{BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.Approach)}]",
                        value: approach
                    );
                }
                if (TryScalar(
                    name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.Orbit),
                    parameters: parameters,
                    value: out var orbit
                )) {
                    RequireUnitInterval(
                        errors: errors,
                        name: $"{itemPath}.scalars[{BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.Orbit)}]",
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
            if (domain.Shared is not null && domain.Kind != WorldNavigationKind.Surface && errors.Count == 0 && !FlockColliderFitsDomain(kit, definition, domain)) {
                errors.Add($"{producerPath} shared navigation domain '{domain.Name}' agentRadius must enclose every collider volume about the body root, including its local offsets.");
            }
            if (parameters.Flock is { } flock) {
                if (flock.ArrivalDistance > domain.ArrivalDistance) {
                    errors.Add($"{producerPath}.flock.arrivalDistance exceeds navigation arrivalDistance.");
                }
                if ((flock.Space == WorldFlockSpace.Tangent) != (domain.Kind == WorldNavigationKind.Surface)) {
                    errors.Add($"{producerPath}.flock.space disagrees with navigation domain '{domain.Name}'.");
                }
            } else {
                RequirePositiveScalar(errors: errors, name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.Approach), parameters: parameters, path: producerPath);
                RequirePositiveScalar(errors: errors, name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.StandoffRadius), parameters: parameters, path: producerPath);
            }
            if (
                TryScalar(name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.StandoffRadius), parameters: parameters, value: out var standoff) &&
                float.IsFinite(f: standoff) &&
                standoff > domain.ArrivalDistance
            ) {
                errors.Add(item: $"{producerPath}.scalars[{BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.StandoffRadius)}] ({standoff}) cannot exceed navigation domain '{domain.Name}' arrivalDistance ({domain.ArrivalDistance}); otherwise the producer stops before advancing its waypoint.");
            }

            if (domain.Kind == WorldNavigationKind.Surface) {
                if (motionProgram is not null && !motionProgram.RequiresRole(role: ChannelRole.MoveAdvance)) {
                    errors.Add(item: $"{producerPath} targets surface navigation domain '{domain.Name}', but kit '{kit.Name}' bodyMotionProgram consumes no MoveAdvance role.");
                }
                continue;
            }

            if (parameters.Flock is null) {
                RequirePositiveScalar(errors: errors, name: BodyProducerParameterVocabulary.Name(parameter: BodyProducerParameter.AltitudeGain), parameters: parameters, path: producerPath);
            }
            var directVertical = motionProgram is not null && (
                motionProgram.Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity) ||
                (motionProgram.Contains(operation: BodyMotionOp.ApplyHold) && (kit.Motion.Holds?.Any(predicate: hold => hold.Thrust > 0f) ?? false))
            );
            var mediumVertical = domain.Kind == WorldNavigationKind.Medium && motionProgram?.Contains(operation: BodyMotionOp.ApplyHold) == true && (kit.Motion.Holds?.Any(predicate: hold => hold.Bond == BodyHoldBond.Medium) ?? false);
            if (!directVertical && !mediumVertical) {
                errors.Add(item: $"{producerPath} targets {domain.Kind.ToString().ToLowerInvariant()} navigation domain '{domain.Name}', but kit '{kit.Name}' bodyMotionProgram has no compatible vertical consumer (ComputeLocalTargetVelocity, an ApplyHold row's own thrust, or a medium ApplyHold).");
            }
            if (!definition.Channels.Any(predicate: channel => channel.Role == ChannelRole.MoveUp)) {
                errors.Add(item: $"{producerPath} targets {domain.Kind.ToString().ToLowerInvariant()} navigation domain '{domain.Name}', but the world declares no MoveUp channel.");
            }
        }
    }
    // A kit's shaping table (SIM-AFFECTING): each row authors exactly one of along (alone for the whole-vector
    // response law, or paired with across for the drive decomposition) or dynamics — never both, never neither; each
    // row's gate admits the shaping-gate predicate vocabulary (body facts plus held); and a null (always) gate before
    // the final row makes every later row unreachable. A named dynamics row needs the world's own simulation rate to
    // compile its step-width coefficients, so a rate-0 (resident, non-stepping) world refuses a row naming one by the
    // same door a dangling name refuses through.
    private static void ValidateShaping(IReadOnlyList<WorldShaping>? shaping, string path, ISet<string> channelNames, ISet<string> dynamicsNames, int simulationRateHz, List<string> errors) {
        if (shaping is not { Count: > 0 } rows) {
            return;
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var rowPath = $"{path}[{index}]";

            if (row is null) {
                errors.Add(item: $"{rowPath} is required.");

                continue;
            }

            ValidateMotionGate(
                predicate: row.When,
                channelNames: channelNames,
                path: $"{rowPath}.when",
                errors: errors
            );

            if (
                (row.When is null) &&
                (index < (rows.Count - 1))
            ) {
                errors.Add(item: $"{rowPath}.when is the unconditional row (omitted) but is not last — every later row is unreachable.");
            }

            var hasAlong = (row.Along is not null);
            var hasDynamics = (row.Dynamics is { Length: > 0 });

            if (row.Dynamics is { Length: 0 }) {
                errors.Add(item: $"{rowPath}.dynamics is empty — name a dynamics row or omit it.");
            }

            if (hasAlong && hasDynamics) {
                errors.Add(item: $"{rowPath} authors both along and dynamics '{row.Dynamics}' — a shaping row selects exactly one.");
            } else if (!hasAlong && !hasDynamics) {
                errors.Add(item: $"{rowPath} requires exactly one of along or dynamics (neither is authored).");
            }

            if (row.Across is not null) {
                if (!hasAlong) {
                    errors.Add(item: $"{rowPath}.across is authored without along — the drive decomposition needs a longitudinal facet to pair with.");
                }

                if (row.Across.Lateral is { } lateral) {
                    RequirePositiveFixed(
                        value: lateral,
                        name: $"{rowPath}.across.lateral",
                        errors: errors
                    );
                }
            }

            if (row.Along is { } along) {
                if (along.Engage is { } engage) {
                    RequirePositiveFixed(
                        value: engage,
                        name: $"{rowPath}.along.engage",
                        errors: errors
                    );
                }
                if (along.Release is { } release) {
                    RequirePositiveFixed(
                        value: release,
                        name: $"{rowPath}.along.release",
                        errors: errors
                    );
                }

                if (row.Across is not null) {
                    if (along.ReversalRate is { } reversalRate) {
                        RequirePositiveFixed(
                            value: reversalRate,
                            name: $"{rowPath}.along.reversalRate",
                            errors: errors
                        );
                    }
                    if (along.BackwardSpeed is { } backwardSpeed) {
                        RequireNonNegative(
                            value: backwardSpeed,
                            name: $"{rowPath}.along.backwardSpeed",
                            errors: errors
                        );
                    }
                } else {
                    if (along.ReversalRate is not null) {
                        errors.Add(item: $"{rowPath}.along.reversalRate is authored without across — whole-vector shaping never reads a drive reversal rate; omit it.");
                    }
                    if (along.BackwardSpeed is not null) {
                        errors.Add(item: $"{rowPath}.along.backwardSpeed is authored without across — whole-vector shaping never reads a drive backward speed; omit it.");
                    }
                }
            }

            if (
                hasDynamics &&
                RequireDeclared(
                declaredSet: dynamicsNames,
                errors: errors,
                field: "dynamics",
                path: rowPath,
                rowNoun: "dynamics",
                value: row.Dynamics
            ) &&
                (simulationRateHz <= 0)
            ) {
                errors.Add(item: $"{rowPath}.dynamics '{row.Dynamics}' cannot compile — the world authors no simulation rate (simulation.rateHz), and a follower's coefficients are bound to one step size.");
            }

            RequirePositiveFixed(
                value: row.TurnScale,
                name: $"{rowPath}.turnScale",
                errors: errors
            );
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
    /// <summary>The tuning facets a body motion program's selected operations read from a kit's declared
    /// <see cref="WorldMotion"/> row — the validator's own mapping (never convention; see
    /// <see cref="RequiredMotionTuningFacets"/>/<see cref="SuppliedMotionTuningFacets"/>) that a new operation must
    /// extend. A declared row missing a facet an operation still reads refuses by name at validation instead of the
    /// operation reading a silent zero at runtime.</summary>
    [Flags]
    private enum MotionTuningFacet : ushort {
        None = 0,

        /// <summary>Speed/Turn — read unconditionally by every Motion-kind body motion program.</summary>
        Speed = 1,

        /// <summary>The <c>shaping</c> table (<see cref="BodyMotionOp.ShapeVelocity"/>). Supplied only by a kit
        /// that authors at least one row.</summary>
        Shaping = 8,

        /// <summary>The ordered hold list (<see cref="BodyMotionOp.ResolveHold"/>/<see cref="BodyMotionOp.ApplyHold"/>)
        /// — the only spelling of a vertical channel, so every Motion-kind kit authors at least one row (see
        /// <see cref="ValidateMotionRow"/>'s own unconditional check, which does not route through this facet
        /// mechanism since the requirement holds whether or not a program even selects the two ops).</summary>
        Holds = 512,
    }
}
