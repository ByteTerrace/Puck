# A kit's `motion` row: holds and shaping

Part of [`puck.world.def.v1`](documents.md). Field names and defaults are
generated (`puck schema`, or `Assets/worlds/schema/kits.schema.json`); this
file is the decision/derivation prose the schema cannot state.

**Kit motion row (`WorldKit.Motion`, a `WorldMotion` row).** A kit declares
its motion tuning, alongside `BodyMotionProgram` (which operations run each
tick) — a flat record (`WorldMotionTuning.cs`), authored under `motion`.

The row carries the movement platform every kit reads — `Speed` (`WorldSpeed`:
`value`, an optional `envelope` clamp, and an optional `held` multiplier
channel — a kart's "boost") and `Turn` (`WorldTurn`: `rate`, an optional
speed-scaled authority curve — `referenceSpeed`/`falloff` — `pitchRate` for a
drive kit's flying variant, and `maxPitch` — the radian ceiling the flying
variant's climb/dive attitude is clamped to, unread while `pitchRate` is
zero, defaulting to the engine's old hardcoded clamp) — plus
`MoveFrame`/`FacingSnap`, and two rows beside them, each supplying its own
tuning facet and each read by its own operations: `Holds` (below; the hold
LIST is mandatory — the hold list is the only spelling of a vertical channel,
so a Motion-kind kit authoring none refuses by name — while
`ResolveHold`/`ApplyHold` are selected like any other op) and `Shaping`
(required only when the program selects `ShapeVelocity`). Two more optional
rows on `WorldMotion` itself carry feel the engine used to hardcode, each
defaulting to the old constant bit-for-bit when omitted: `upTurn`
(`WorldUpTurnRates`: `field`/`contact`, the half-angle-per-second ceilings on
how fast a solved gravity field, respectively a measured ground-contact
normal, may turn the body's up axis) and `obstruction`
(`WorldObstructionLatch`: `displacement`/`idleThreshold`/`graceSeconds`, the
non-walkable contact witness's persistence — how far the body must move to
count as moved on, the driven-input floor below which it counts as idle, and
how long an unrefreshed latch survives a solver pass reporting no push). The
positive fixed rates must survive Q48.16 compilation, `displacement` must do
so after squaring, and `graceSeconds` must be a positive exact whole engine-
tick duration. A third scalar, `groundStick`, is the inward speed (world
units/second) a
grounded body on a curving surface is held against; it is independent of
`Speed` — a kit's own resolved move speed measurably over-corrects a shallow
slope climb (the bias converts to downhill drift under depenetration faster
than to held contact) — and also defaults to the engine's old constant.
In-medium locomotion is a kit authoring a `bond: "Medium"` hold row; a kart
is a kit whose shaping table carries an `across` row.

**`shaping` (`WorldShaping[]`) — the unified velocity-shaping table.** One
row shape serves the whole-vector response law, the anisotropic drive
decomposition, and a named second-order follower: `{ "when": <predicate,
optional>, "along": { "engage"?, "reversalRate"?, "release"?, "backwardSpeed"? }, "across":
{ "lateral"? }, "dynamics": "<row>", "turnScale": 1 }`. Rows evaluate in order,
first open gate wins; `when` admits the shaping-gate predicate vocabulary —
body-fact kinds (`now`/`recently`/`all`/`any`/`not`) plus `held` (`{ "held":
"<channel>" }` — the named composition channel's own live read at or above
its declared threshold, resolved against the world's channel table at
kit-compile time; legitimate only here). Exactly one of `along` or `dynamics`
is authored per row; `across` is legitimate only beside `along`. An omitted
convergence rate means exact, immediate convergence. An explicit rate must be
positive; zero is never a hidden spelling of "instant" or "disabled". `reversalRate`
and any authored `backwardSpeed` are refused on a whole-vector row because that law
does not read drive-only facets.

A row without `across` shapes the whole vector through the engage/release
response law — `engage` while the stick is deflected, `release` while
centered, with the shared recency clocks its `when` gate's `recently`
predicates read. A row with `across` runs the anisotropic drive
decomposition instead, the same body-frame longitudinal/lateral/residual
lanes converging each at its own authored rate: `along.engage` while throttle commands
more speed, `along.reversalRate` while back-throttle opposes forward travel,
`along.release` toward rest with throttle centered (and the over-speed
bleed), `along.backwardSpeed` the backward target speed full back-throttle
converges on from rest, and `across.lateral` the lateral convergence rate
toward zero slip. `turnScale` multiplies the turn tuning's own authority
curve while this row governs — `1` (the default) for an ordinary row, and a
held drift row's own tightened arc. A `dynamics` row names a `dynamics`-
section row (a pole-matched second-order follower — see
[documents-render.md](documents-render.md)) shaping velocity instead of either mechanism; it compiles
once per kit (`WorldKit.Compile`) against the world's own
`simulation.rateHz` — a world authoring no simulation rate cannot compile
one and refuses by name. The follower's state lives in `WorldBody` as
ordinary sim state, included in whatever the body snapshot/checkpoint
covers; changing which mechanism a row uses, or retuning a live `dynamics`
row, is expected to change replay hashes. A drift/boost row is authored as
an ordinary row gated on `held` — never a bespoke mechanism — so it must sit
AHEAD of the row it overrides.

What an anisotropic shaping row does NOT carry is what the motion row and its holds already
name: the forward speed full throttle converges on is `speed.value` (bounded
by `speed.envelope`, scaled by `speed.held.multiplier` while its channel
reads held), the steering rate at full authority is `turn.rate`, and gravity
is the held row's own `gravity` arc. One shaping table serves the ground,
hover, and air variants: a contact-pinned variant pairs an `across` row with
a Surface `Gravity` hold row, a flying variant a Free `Lift` row (`lift: 1`)
and a positive `turn.pitchRate`, which is what decides vertical contact
ownership per the seam's rule. Validation
(`WorldDefinitionValidator.ValidateShaping`): every authored convergence rate
positive; `along.backwardSpeed`, when present, non-negative; `across` refused without `along`,
`turn.falloff` in `[0, 1]`, `turn.pitchRate` non-negative, and a `held` gate
naming a resolvable channel. `DriveLawTests` pins the drive family, and
`ShapingRowLawTests` the whole-vector and dynamics-row families, to
recorded 240-tick fixed-point traces whose discriminating controls perturb
one facet each.

A worked kart kit, in `.puck` (a fragment — `bodyMotionProgram`, the
`boost`/`drift` channels, and the `kart-drive` program itself live in the
declaring world; a standalone `--validate` on this excerpt alone refuses
those three names, exactly as the original JSON excerpt was never a
standalone document either):

```
kits {
    rows [
        {
            name: "kart"
            bodyMotionProgram: "kart-drive"
            motion {
                speed {
                    value: 16
                    envelope {
                        min: 16
                        max: 16
                    }
                    held {
                        channel: "boost"
                        multiplier: 1.5
                    }
                }
                turn {
                    rate: 2.4
                    referenceSpeed: 4
                    falloff: 0.55
                }
                holds [
                    {
                        name: "ground"
                        bond: "Surface"
                        cone [0, 60]
                        hold: "Gravity"
                        reach: 1.2
                        gravity {
                            rise: 14
                            fall: 26
                        }
                        envelope {
                            sinkSpeed: 30
                        }
                    }
                    {
                        name: "air"
                        bond: "Free"
                        hold: "Gravity"
                        gravity {
                            rise: 14
                            fall: 26
                        }
                        envelope {
                            sinkSpeed: 30
                        }
                    }
                ]
                shaping [
                    {
                        when: held(channel: "drift")
                        along {
                            engage: 7
                            reversalRate: 18
                            release: 4
                            backwardSpeed: 5
                        }
                        across {
                            lateral: 6
                        }
                        turnScale: 1.4
                    }
                    {
                        along {
                            engage: 7
                            reversalRate: 18
                            release: 4
                            backwardSpeed: 5
                        }
                        across {
                            lateral: 22
                        }
                    }
                ]
            }
        }
    ]
}
```

Compiles to (`puck compile kart.puck`, structural — no `--validate` on this
excerpt for the reason above):

```json
{
  "kits": {
    "rows": [
      {
        "name": "kart",
        "bodyMotionProgram": "kart-drive",
        "motion": {
          "speed": { "value": 16, "envelope": { "min": 16, "max": 16 }, "held": { "channel": "boost", "multiplier": 1.5 } },
          "turn": { "rate": 2.4, "referenceSpeed": 4, "falloff": 0.55 },
          "holds": [
            { "name": "ground", "bond": "Surface", "cone": [0, 60], "hold": "Gravity", "reach": 1.2, "gravity": { "rise": 14, "fall": 26 }, "envelope": { "sinkSpeed": 30 } },
            { "name": "air", "bond": "Free", "hold": "Gravity", "gravity": { "rise": 14, "fall": 26 }, "envelope": { "sinkSpeed": 30 } }
          ],
          "shaping": [
            { "when": { "$type": "held", "channel": "drift" }, "along": { "engage": 7, "reversalRate": 18, "release": 4, "backwardSpeed": 5 }, "across": { "lateral": 6 }, "turnScale": 1.4 },
            { "along": { "engage": 7, "reversalRate": 18, "release": 4, "backwardSpeed": 5 }, "across": { "lateral": 22 } }
          ]
        }
      }
    ]
  }
}
```

Note the wrapping shape: `kits` lowers to `{"rows": [...]}` (`WorldKitsSection`
— the same dealt-row shape `looks`/`placements` use), never a bare array; the
`.puck` sugar for it is the named block `kits { rows [ ... ] }`, not
`kits [ ... ]`.

with the program `[ResolveDriveFrame, ResolveHold, ShapeVelocity,
RunActionTriggers, ApplyHold, IntegratePlanarAndVerticalVelocity, CommitPose]`
(op ORDER in the authored list is inert — `CompiledBodyMotionProgram` groups the
selected set into its intrinsic phases) — the same op list every grounded kit
runs, since the hold list is where a kart's gravity lives.

A kit with no shaping row (empty or absent) refuses validation by name when
its program selects `ShapeVelocity`, exactly as an empty/absent hold list
does for `ResolveHold`/`ApplyHold`; a kit whose program never selects it (a
free-flight kit that owns its whole velocity channel directly) may author
none. `Speed.Held` is a HELD (not edge-triggered) channel that scales the
resolved planar speed while it reads held, default `null` (no held
multiplier) — a shaping row's boost is this seam under that name, never a
second channel; resolved to `FixedSpeed.HeldOrdinal` the same way a producer's
`BodyProducerParameter.Press` channel argument resolves its own ordinal
(`CompiledBodyProducer.Channel`), since a channel name needs the world's
compiled channel table and a body's own compile step has none. `MoveFrame` (`MotionMoveFrame.Heading` / `.World` default) and
`FacingSnap` — `Heading` is tank controls; `World` (every kit that never
sets this field) treats `MoveAdvance`/`MoveStrafe`
as ALREADY-WORLD-FRAME axes (the seat's client resolves its camera yaw into
the submitted intent BEFORE the wire — determinism: the sim never reads a
camera pose) and, with `FacingSnap` on, snaps the body's facing to
`Atan2` of the commanded direction every tick carrying input, no ramp — the
camera-frame 3D-platformer feel a `FacingSnap` kit authors.
Under `World` a seat's aim elevation also splits the commanded
forward into planar and vertical channels client-side; the explicit `MoveUp`
channel is orthogonal and stays live regardless of `MoveFrame`.

**Shaping-row ORDER shadows regimes — author air rows first.** The shaping
table evaluates in order, first open gate wins, and a `recently Grounded`
window (`0.09s` ≈ 21 ticks at 240 Hz) stays open through the RISE of every
jump — so a recently-Grounded row above a `now Rising` row governs the first
~21 airborne ticks with GROUND rates (a stick released at takeoff bleeds
momentum at the ground `release` rate; air steering briefly runs the ground
`engage` rate). Author `now Rising` / `now Falling` rows ABOVE any
recently-Grounded row; a plain unconditional row then covers grounded ticks.
`ShapingRowLawTests`' walker kit is the worked example of the corrected
order, with the measured arc numbers in its motion row.

`WorldDefinitionValidator` cross-checks the kit's `BodyMotionProgram`
against its declared motion row: an operation the program selects that reads a
tuning facet (`MotionTuningFacet`) the row doesn't supply refuses
BY NAME. The row supplies `Speed` (Speed/Turn together) unconditionally;
`Holds` and `Shaping` are each supplied CONDITIONALLY — only when the kit's
`holds` list is non-empty, or its `shaping` list is non-empty — so a
program selecting `ResolveHold`/`ApplyHold` or `ShapeVelocity` against a kit
authoring none refuses by that facet's name. Separately, and unconditionally:
a Motion-kind kit authoring no holds at all refuses by name outright,
whatever operations its program selects — the hold list is the only
spelling of a vertical channel, so even a kit with no vertical law of its
own still authors one row of kind `None`. A world whose kit authors a
`Medium` hold row with no medium lattice row (`state.world[].lattice.medium`)
refuses at boot. A `BodyMotionOp` reading a further facet owes
`RequiredMotionTuningFacets` and `SuppliedMotionTuningFacets` an entry —
never a hunt.

A seated player's live profile overrides the kit's `Speed.Value`
(feel stays real-time under `profile.set`/`identity.motion`);
`WorldSpeed.Envelope` is the world's own counter-pin — an
authored `MotionScalarEnvelope { min, max }` that clamps the RESOLVED
speed at the seat-time read (`WorldBody.ResolveMoveSpeed`, before the
program ever sees it), regardless of whether it came from the profile or
the profileless fallback. Absent (the default) is wide-open, today's
behavior exactly; `min == max` pins the effective speed outright; the
validator refuses `min > max` and refuses a kit whose OWN `speed.value`
falls outside its own envelope, by name. `identity.show`'s
`moveEffective=` echoes what the sim actually applied beside `move=` (the
profile's raw request) — the two diverge only when an envelope is
narrower than what the profile asked for. `MotionScalarEnvelope` is the
reusable shape every overridable scalar adopts, never a bespoke
bound.

`ResolveMoveSpeed` is ONE law for every kit — the seated profile's claimed rate,
else the kit's own, clamped by `Speed.Envelope` — shared by the sim and every
read-back so the two can never disagree. A kit that means to pin its speed
against any profile authors `min == max` rather than opting out of the profile
read, and a held speed multiplier (a shaping row's boost) multiplies AFTER the
clamp, on the resolved value: the envelope pins the base rate, the boost
rides on top. `ResolveTurnAuthority`'s falloff anchor and every shaping
row's own commanded speed both read the SAME resolved value
(`scratch.MoveSpeed`, filled once before phase 0), so a clamped kit's falloff
still reaches its anchor. The validator's own-value check applies to every kit
too: a kit whose `speed.value` falls outside its own envelope refuses by name, so
a live `world.row.set kits …` retune past the cap refuses instead of clamping
silently.

**Holds (`WorldMotion.Holds`, a list of `WorldHold` rows →
`FixedBodyHold[]`) — what may hold a body, in preference order, and the only
spelling of a vertical channel.** A kit authors an ordered list; the
`ResolveHold` operation takes the first row the world offers and `ApplyHold`
applies that row's vertical law and its own `thrust`. A Motion-kind kit
authoring none refuses validation by name — even a kit with no vertical law of
its own still authors one row of kind `None`, since the hold list is the only
spelling of a vertical channel, never simply absent, whatever operations the
program selects.

A worked walker's `holds` list, in `.puck` (again a fragment — `walk`, and
the `jump`/`stamina` names, live in the declaring world):

```
kits {
    rows [
        {
            name: "walker"
            bodyMotionProgram: "walk"
            motion {
                speed {
                    value: 5
                }
                turn {
                    rate: 3
                }
                holds [
                    {
                        name: "wall"
                        bond: "Surface"
                        cone [60, 120]
                        hold: "Pull"
                        pull: 1
                        reach: 0.8
                        speed: 2
                        upLean: 0
                        forward: "Heading"
                        onDrive: true
                        driveAlignment: 0.5
                        release: "jump"
                        spend {
                            state: "stamina"
                            ratePerSecond: 1
                        }
                    }
                    {
                        name: "ground"
                        bond: "Surface"
                        cone [0, 60]
                        hold: "Gravity"
                        reach: 1.2
                        gravity {
                            rise: 28
                            fall: 46
                        }
                        envelope {
                            sinkSpeed: 40
                        }
                    }
                    {
                        name: "air"
                        bond: "Free"
                        hold: "Gravity"
                        gravity {
                            rise: 28
                            fall: 46
                        }
                        envelope {
                            sinkSpeed: 40
                        }
                    }
                ]
            }
        }
    ]
}
```

Compiles to (`puck compile walker.puck`, structural):

```json
{
  "kits": {
    "rows": [
      {
        "name": "walker",
        "bodyMotionProgram": "walk",
        "motion": {
          "speed": { "value": 5 },
          "turn": { "rate": 3 },
          "holds": [
            { "name": "wall", "bond": "Surface", "cone": [60, 120], "hold": "Pull", "pull": 1,
              "reach": 0.8, "speed": 2, "upLean": 0, "forward": "Heading",
              "onDrive": true, "driveAlignment": 0.5, "release": "jump",
              "spend": { "state": "stamina", "ratePerSecond": 1 } },
            { "name": "ground", "bond": "Surface", "cone": [0, 60], "hold": "Gravity", "reach": 1.2,
              "gravity": { "rise": 28, "fall": 46 }, "envelope": { "sinkSpeed": 40 } },
            { "name": "air", "bond": "Free", "hold": "Gravity",
              "gravity": { "rise": 28, "fall": 46 }, "envelope": { "sinkSpeed": 40 } }
          ]
        }
      }
    ]
  }
}
```

`bond` is `Surface` (a contact-field face whose normal makes an angle inside
`cone` degrees with GRAVITY-up — 0 a floor, 90 a wall, 180 a ceiling; the cone
is measured against gravity-up, never the body's own leaned up), `Free` (no
surface at all), or `Medium` (the world's own field-lattice column — the world
either offers a medium where the body is or it does not, so the bond carries no
cone and no reach, and takes a `medium` law instead:
`{ idleDrift, equilibriumOffset, settleRate }` — `settleRate` is the one gain
that turns the equilibrium error into a target velocity; the governing shaping
row's own `along`/`dynamics` facet then rate-limits the body's actual velocity
toward that target the same way it rate-limits every other channel). `hold` is
`Gravity` (gravity holds the body
against the face — the walkable case, integrating the row's own `gravity`
arc), `Pull` (a pull of `pull` u/s toward the face applied as a POSITIONAL
standoff, gravity suspended while it holds), `Lift` (a fraction `lift` of
gravity cancelled — 1 hovers, bleeding whatever the vertical channel carries
back to rest at the row's own `gravity.rise` rate rather than integrating the
arc), or `None`. `gravity` (`{ rise, fall }`, u/s², u/s²) is the row's own
vertical arc — required on a `Gravity` or `Lift` row, refused on a `Pull` row
and on a `Medium` bond (a medium displaces by its own law); the world's own
solved gravity field, where one is authored, overrides the MAGNITUDE but
keeps the row's `rise:fall` ratio as the arc's asymmetry. `envelope`
(`{ riseSpeed?, sinkSpeed }`, u/s each) is the vertical-channel bound a
`Gravity`/`Lift` row's own terminal fall speed and a `Medium` row's terminal
rise/sink speeds share — the SAME field family a document-wide speed ceiling
walks, rather than three separately-authored numbers a caller has to
`Math.Max` across. Required for a `Medium` bond (both directions) and for a
`Gravity`/`Lift` row short of full lift (sink only — that arc never clamps a
rise); refused otherwise, including on a full-lift row (`lift: 1`), whose
channel decays rather than clamps. `thrust` (a fraction of the kit's resolved
move speed the `MoveUp` role commands vertically, `[0, 1]`, default `0`)
applies in EVERY bond: a non-`Medium` row commanding thrust takes the
vertical channel outright for the tick, clearing the ballistic carry, while a
`Medium` row's own thrust folds into its displacement law's convergence
instead — the medium's drift and the body's own MoveUp thrust are summed
BEFORE that convergence runs, so nothing writes the vertical channel twice,
and it publishes `InMedium`/`AtMediumBand`. `speed` (u/s along the row's
tangent plane, absent rides the kit's own resolved move speed), `reach` (how
far a surface row's probes search; required positive), `onDrive` +
`driveAlignment` (take the row by driving into a face in its cone), `release`
(a declared channel whose HELD read drops the row — no latch, so holding it
down keeps the body off the face), and `spend` (drain a body-lane Counter
slot at a rate; the row becomes ineligible the tick the slot cannot pay, and
a world's own rules refill it or trade for it — the engine has no stamina
concept of its own).

A pull owns the whole tangent-plane velocity, rise included; the tick it ends
(released, spent out, or its face lost) that rise is split against gravity-up
and carried into the ballistic channel, so a body letting go mid-climb keeps
the climb's momentum instead of dropping from rest.

Frame rules per row: `upLean` in `[0, 1]` blends the body's up axis from
gravity-up toward the face normal (0 keeps a body upright on a wall, 1 lays it
on the face). A pull's drawn axis is TURNED into its lean, never snapped, at the
rate a body turns over its own span — the row's `speed` over the collider's
probe height plus standoff, rad/s, derived and not authored — so a face change
(floor to wall to ceiling) is a turn, and the axis returns to the contact axis
the same way when the row ends; a `gravity` hold's drawn axis stays with its
lean, whose contact axis is bounded already. **Whether that lean also carries the body's CONTACT axis is
decided by the hold's KIND, never by the lean.** A `gravity` hold is one the
world's own gravity presses onto its face, so the face IS the ground the solver
should stand the body on and the axis leans with it, bounded through the same
accumulator a measured contact normal is adopted by — a kart on a loop. A `pull`
hold holds the body instead, gravity is suspended, and leaning the contact axis
there would tell the solver that the floor under the body is a ceiling and that
falling is upward: the floor stops depenetrating and a released body flies off.
So a pull's lean is the body's FRAME — the plane it travels in and the attitude
it is drawn at (`scratch.AttitudeUp`, which every attitude writer including the
facing snap composes about) — while the contact axis stays with the ambient
resolve. `forward` (`Heading`/`Intent`/`Velocity`) chooses what that drawn
attitude tracks inside the row's own frame. Movement rides the face's own tangent plane: forward is gravity-up
projected onto the face ("up the face"), right completes it, so
`ComputePlanarTargetVelocity` needs no new operation. A face whose normal is
parallel to gravity-up leaves that tangent undefined, and there the ordinary
frame stands.

**Resolution order, per tick.** What the body is actively DRIVING into outranks
what it happens to be resting on, so the `onDrive` pass runs first over the
ordered list. Otherwise the list decides, first match wins, with the row
already held evaluated by whether its own face is still there (the directed
tracking probe along that face's inward normal) rather than by a fresh take —
so a row authored EARLIER still wins from where the body stands, which is why
a walker authors `wall` before `ground`. A held non-walkable row also ends when
the contact resolve has stood the body on something walkable and the body has
stopped pulling itself along the face. When a held face ENDS while the body is
still driving forward, one further pass reaches PAST the edge — up the last
tangent by a body span and in past the face by a body width — and the body
arrives one body span off whatever it finds there; that is the whole of the
ledge transition, with no mantle phase.

Every probe is DIRECTED, for the same reason a pull's always was: an undirected
nearest-surface query on a world whose floor, walls, ramps and overhangs are one
holdable placement answers with the floor a body is standing on.
`IContactField.TryHoldableSurfaceAlongDirection` is that query; both providers
answer it, the field by ray march (`RayHit.Normal` is documented ZERO — the
surface orientation is a separate `TryFieldGradient` read one step back along
the ray). Which surfaces admit a hold at all is the collision row's
`defaultHold` (world-level) composed with a placement's own
`grip: { holdable: … }` override — the placement's own surface-holdability
facet, a distinct concept from a hold row's `Pull` kind despite the shared
word: `WorldPlacementGrip` says whether a face can be held AT ALL, never how.

Validation (`WorldDefinitionValidator.ValidateHolds`): unique row names; a cone
required for a `Surface` row and refused for a `Free` one, finite, inside
`[0, 180]`, increasing; a positive `reach` on a `Surface` row; `upLean` and
`driveAlignment` in `[0, 1]`; a positive `pull` on a `Pull` row and `Pull`/
`onDrive` requiring `Surface`; `lift` in `[0, 1]`; `gravity` required with a
positive `rise`/`fall` on a `Gravity` or `Lift` row and refused elsewhere;
`envelope` required (sink speed positive; a `Medium` bond also requires a
positive rise speed) on a `Medium` bond or a `Gravity`/`Lift` row short of
full lift, and refused otherwise; `thrust` in `[0, 1]`; `release` naming a
declared composition channel; `spend.state` naming a declared body/identity
Counter slot and a positive `spend.ratePerSecond`. Both `Holds` and `Drive` are
CONDITIONALLY-supplied facets — a kit authoring an empty or absent `holds` list
refuses a Motion-kind program outright (a fact checked ahead of the facet
mechanism, since it holds whatever operations the program selects), and a
drive program against a kit authoring no `drive` row is what the
`MotionTuningFacet` gate refuses by name. A Motion-kind kit's `holds` list must
also author at least one UNCONDITIONAL row — a `Free` bond with no `release`
and no `spend` — so `ResolveHold` always has a row it can fall to once every
earlier candidate goes ineligible and `ApplyHold` is never left with no current
hold to read; and a program selecting `ApplyHold` without `ResolveHold`
refuses by name, since `ApplyHold` applies whatever row `ResolveHold`
selected. A `Medium` row does NOT count toward this: `ResolveHold` takes it
only where the world's own lattice offers a medium column at the body, so a
Medium-only list still leaves a body outside its medium with nothing to fall
to — every kit authoring a Medium row also authors a trailing `Free` row for
that case.

A `Medium` row is the ONLY spelling of the medium law — `ApplyHold` runs it
against the row `ResolveHold` took, and `WorldMediumLawTests` pins it to a
recorded 240-tick fixed-point trace. The garden's `fishKit` (`puck.world.json`) is
the worked example: a kit whose `fishMotion` program runs
`ResolveHold`/`ApplyHold` over a `water` row carrying the five medium facets,
and a trailing `air` row (`Free`, `Gravity`) for the water's own dry fallback.
That row authors no `thrust` because its wander producer never writes the
`MoveUp` role; `WorldMediumLawTests`' own fixture is the thrust-carrying
example.

Read back with `body.hold` (`[body.hold: body:<n> hold=<name|none>
normal=(x, y, z) spend=<left|n/a>]`). The current row index, its anchor and
normal, and the spend accumulator's remainder are simulation state: captured in
`IntegrationResidue`, carried through `WorldAuthorityCheckpointCodec`, and part
of the replay hash.

**A kit's `tether` facet (`WorldTether` → `FixedWorldTether`) — an aimed
distance-cap rope, beside `rigid`/`carry`.** Absent (`null`) refuses
`body.attach`/`body.detach`/`body.reel` by name for that kit's bodies, the
same presence-is-the-switch convention `rigid`/`carry` carry. A body's attach
state is `m_tether is not null` (`WorldBody.Tether.cs`), read directly off the
intent (never through a kit's action table) and echoed by `body.tether`; the
facet itself is echoed per kit by `world.kits`. Its fields are the aim ceiling
and cone (`maxAnchorDistance`/`aimHalfAngleDegrees`), the rope
(`lengthRate`/`minLength`), the release scale (`releaseVelocityScale`), the
three channel names, and an optional `modeState` counter-slot name the facet
writes `1`/`0` to while attached/not — resolved to an ordinal at kit compile
time, the camera program's `select` op keys off it like any `state.<row>`
value. Positive numeric values must survive their Q48.16 compilation as
nonzero (including the cone's degree-to-radian conversion). Surface holds are
not authored here.

**Body facts on the wire (`BodyFacts`, `Puck.Physics.Motion`).** The engine
publishes each body's per-tick fact set on `EntitySnapshot.Facts` — one bit
per body-state `ActionFact` (`grounded`, `airborne`, `rising`, `falling`,
`inmedium`, `atmediumband`, `holdingunwalkable` — holding a surface row whose face is
outside the world's own walkable cone — `unsupported` — holding a free row with
lift — and `resting`, written only by the rigid solver once a rigid body's
linear and angular velocity latch to zero; `AffectedBy` has no bit, being a relationship rather than a state). The mask is derived through the SAME
predicate the kit's action gates read, so the snapshot, the gates, and the
`body.where` echo cannot disagree; a decoder refuses an undeclared bit by
name. `WorldSessionMirror.Facts(int)` and `WorldClient.Facts(int)` front it
for presentation, which is how animation keys on regime without the client
deriving one. `body.where` echoes it as `facts=` (lower-case, `|`-joined in
bit order, `none` when empty), followed by `home=(x, y, z)` — the position the
body was ACTIVATED at (a seat's spawn point, an inhabitant's placement plus its
own distribution sample) — then `scale=` (`Server.WorldBody.Scale`'s read-back)
and, for a routed local seat, `anchor=body:<n>` (the seat's currently routed
entity index). A producer's inward pull steers against that home rather than the world origin,
so a population spread over several placements keeps to its own ground
instead of congregating; a teleport moves the body, never its home. Facts are
NOT mutually exclusive: a body can be
grounded and rising in one tick, and a body on a wall reads `airborne|holdingunwalkable`
because contact resolution keeps running under every hold.

`views.seatRig` is a `WorldCameraProgram` — an ordered op list, not a kind
union (see views.md for the op table). Its `dynamics` op names a `dynamics`
row (see [documents-render.md](documents-render.md)) whose second-order response the CALLER applies as a
presentation-only ease on the boom; a program with no `dynamics` op passes
the boom through with no ease — a different mechanism from
`WorldAnchor.Group.SmoothRate`'s exponential ease, which is unrelated and
still authored separately for a group-centroid establishing shot.
