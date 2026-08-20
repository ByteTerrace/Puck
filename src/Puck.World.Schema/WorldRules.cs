using System.Text.Json.Serialization;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>
/// One world-scoped rule: a condition over world facts and the effects that follow — the same primitive a kit's
/// per-body actions already run on (<see cref="ActionPredicate"/>, <see cref="ActionEffect"/>,
/// <see cref="ActionTriggerMode"/>), widened to the world's own scope rather than a sibling engine with its own
/// vocabulary. There is no scheduler and no trigger taxonomy beside this: time is just another fact.
/// </summary>
/// <remarks>
/// <para><b>What lifts unchanged, and what is refused by name.</b> <see cref="Gate"/> reuses
/// <see cref="ActionPredicate"/> as the authored ADT — no new predicate type exists. Two of its five cases are
/// admissible at world scope: <see cref="ActionPredicate.All"/> (a pure combinator) and
/// <see cref="ActionPredicate.CompareState"/> (whose <c>State</c> resolves against the world's <c>state</c> section,
/// or one of <see cref="WorldRuleFacts"/>'s reserved channels). <see cref="ActionPredicate.Now"/>/
/// <see cref="ActionPredicate.Recently"/> read a per-body <see cref="ActionFact"/> that has no meaning without a body,
/// and <see cref="ActionPredicate.TimerElapsed"/> reads a per-body timer slot; all three are refused at compile time,
/// never reinterpreted. <see cref="Effects"/> likewise reuses <see cref="ActionEffect"/>, admitting
/// <see cref="ActionEffect.SetState"/>/<see cref="ActionEffect.AddState"/> (a world state write),
/// <see cref="ActionEffect.Generate"/> (firing a generator row — the join that makes generation and rules one arc),
/// and — riding the same "admit an existing <c>WorldMutation</c> kind into the rule effect set" seam —
/// <see cref="ActionEffect.UpsertHudPanel"/>/<see cref="ActionEffect.RemoveHudPanel"/> (a world rule authors/removes a
/// HUD panel) and <see cref="ActionEffect.UpsertPlacement"/>/<see cref="ActionEffect.RemovePlacement"/> (a world rule
/// spawns/removes a placement row); the velocity/impulse/designate/timer effects remain irreducibly per-body and are
/// refused. <see cref="ActionEffect.Save"/> admits on different terms again — not an existing mutation kind at all,
/// but engine I/O (a session snapshot of the world to its own file) with no document effect and no
/// <c>WorldMutation</c> of its own; see its own remarks for why that is not a door.</para>
/// <para><b>Addressing is a (row, key) pair.</b> A world-scope <c>setState</c>/<c>addState</c>/<c>compareState</c>
/// names the row in <c>State</c> and the cell in <c>Key</c>; a null key means the row's slot cell, which a keyed row
/// does not have and is refused for rather than silently reading the row's first cell. Rules therefore reach keyed
/// rows — an inventory, a per-player tally — not only scalars. A read operand (a gate subject, a comparand, a
/// <c>fromState</c>) must additionally address a cell the row declares — an undeclared cell would read 0 forever
/// with no refusal anywhere, so it refuses at compile as <see cref="WorldRuleRefusal.StateCellUndeclared"/>; write
/// destinations mint their cells and stay exempt. Because rules recompile under whole-document revalidation, removing
/// a cell a rule reads refuses the removal, naming the rule.</para>
/// <para><b>A comparand can move — periodicity, cooldowns, round boundaries.</b> A
/// <see cref="ActionPredicate.CompareState"/>'s comparand is either an authored constant (<c>Value</c>) or a second
/// live operand (<c>ComparandState</c>/<c>ComparandKey</c>: a row or reserved channel, resolved through the same
/// operand walk as the primary side) — never both, never neither. That one widening is the whole periodicity
/// vocabulary; there is no scheduler and no new fact kind. Two patterns, and the footgun between them:</para>
/// <para><b>Every N ticks (moving threshold vs <c>$tick</c>).</b> Gate <c>$tick &gt;= nextBeat</c> against an
/// <c>int</c> schedule row the rule's own effect advances by N on fire (<c>addState nextBeat += N</c>),
/// <see cref="ActionTriggerMode.Edge"/>. The advance lands synchronously inside the same <c>EvaluateWorldRules</c>
/// pass, so for N &gt;= 2 the gate self-closes the tick after it opens and the rule fires floor(elapsed/N) times over
/// a window. Edge is wanted for the ordinary reason (see <see cref="WorldRule.Mode"/>) and a second one specific to a
/// moving threshold: if the advance is ever denied (a grant revoked mid-session) the gate stays stuck open, and
/// Edge's latch — armed the instant the gate opened, before the effect that was to close it ever ran — stops the
/// runaway re-fire that <see cref="ActionTriggerMode.Level"/> would spam. A period of exactly 1 tick never closes its
/// own gate (tick and schedule move in lockstep) and wants Level, not Edge, to keep firing at all.</para>
/// <para><b>A cooldown is not a <c>$tick</c> threshold — it is a relative countdown.</b> The tempting spelling — a
/// <c>nextAllowed</c> row set to <c>$tick</c>+N on use, gated <c>$tick &gt;= nextAllowed</c> — is a footgun for a
/// request-gated ability: once a session has accrued background ticks, <c>$tick</c> already sits far past any freshly
/// set <c>nextAllowed</c>, so the gate is open the instant the request arrives and never spends the cooldown. Build a
/// cooldown as a countdown instead: a <c>NonNegative</c> <c>int</c> row <c>cooldownRemaining</c>, a
/// <see cref="ActionTriggerMode.Level"/> rule gated <c>cooldownRemaining &gt; 0</c> that consumes it each simulation
/// tick with <see cref="ActionEffect.CountdownState"/> (the effect reads that tick's engine-tick step width from the
/// runtime, so it stays rate-independent and saturates a final partial step at zero), and the ability gated on
/// <c>cooldownRemaining &lt;= 0</c>; using the ability re-arms it (<c>setState cooldownRemaining valueSeconds=N</c>
/// or, when <c>N</c> has no terminating decimal spelling, <c>setState cooldownRemaining = &lt;N * 50400&gt;</c> raw).
/// The <c>&gt; 0</c> gate avoids firing an inert effect after the row reaches zero. A relative countdown is immune to
/// absolute-tick drift by construction: it measures elapsed engine ticks, never an absolute deadline.</para>
/// <para><b>An effect can copy a live operand too — the round-boundary reset.</b> <see cref="ActionEffect.SetState"/>/
/// <see cref="ActionEffect.AddState"/> carry the same value/comparand duality <see cref="ActionPredicate.CompareState"/>
/// already does: <c>Value</c> (an authored constant) XOR <c>FromState</c>/<c>FromKey</c> (another row or reserved
/// channel, read live at fire time through the same operand walk the comparand side uses) — never both, never
/// neither. This is what closes the shadow-row footgun a moving-comparand gate otherwise falls into: a rule reacting
/// to <c>round</c> changing by any amount, from any writer (<c>compareState round != roundReflect</c>, a rule that
/// does not itself own the advance), can reset a whole set of other rows to authored literals and resync its own
/// shadow row to <c>round</c>'s current value in the same firing (<c>setState roundReflect fromState=round</c>)
/// instead of a standing <c>addState roundReflect += 1</c> that only tracks a disciplined +1 counter and silently
/// desyncs — gate stuck open, latch held, no further resets, no refusal anywhere — the moment something else advances
/// <c>round</c> by more than one or sets it outright. When the rule that advances the round is itself authored as a
/// rule, the resets can live as ordinary additional effects in that same rule's <c>Effects</c> list instead (a rule
/// is not limited to one row write); the copy operand exists for the decoupled case, where the resetting rule is not
/// the thing that changed the counter. A copy is also the only exact write spelling: <c>Value</c> is a
/// <see langword="float"/>, so an authored literal above 2^24 is already rounded by the time the compiler sees it
/// (16777217 compiles to 16777216), while the copy path carries the source cell's bits through unchanged (see
/// <c>WorldServer.ConvertWorldFactToRaw</c>). A copy reads the same same-tick state a gate does, so an earlier
/// rule's write is visible to a later rule's copy — declaration order decides it, deterministically.</para>
/// <para><b>Coverage, precisely.</b> A rule's effects submit ordinary mutations, so the journal records them and
/// <c>world.undo</c> rewinds them like any other write. The replay tape does not cover them: the tape carries
/// commands, intents and session traffic, and has no mutation arm at all — a rule's writes are re-derived by
/// re-execution on replay, exactly like the world-events feed.</para>
/// <para><b>The trait boundary.</b> A <see cref="WorldStateRow"/>'s slot cell may separately declare
/// <see cref="WorldStateRow.Advance"/>, and a keyed row's own cell may independently declare
/// <see cref="WorldStateCell.Advance"/> (see <c>WorldState.cs</c>) — a continuous accumulation between observations,
/// needing no schedule row and firing no per-tick write. Rules own the discrete half of this vocabulary (a moving
/// $tick threshold, a decrementing countdown); the two compose, never duplicate: a read operand resolves through
/// <see cref="WorldStateReader"/> like every other, which computes an advancing cell's live value rather than its
/// stored base regardless of which of the two traits it carries, so "fire once health regenerates past half" needs
/// only an ordinary Edge rule gated on a cell the trait — not the rule — knows how to advance, and a
/// <c>$reduce:</c>/<c>$argmax:</c>/<c>$argmin:</c> operand over a table of independently advancing cells sees every
/// cell's live value the same way. A rule's own <c>setState</c>/<c>addState</c> effect against such a cell is itself
/// an explicit write, so it re-bases the trait's accumulation exactly like any other explicit set would. The read
/// operand's declared-cell requirement bites here too: an advancing row that has never been written declares no slot
/// cell yet, so a rule reading it refuses at compile (<see cref="WorldRuleRefusal.StateCellUndeclared"/>) until the
/// row carries an authored or written base.</para>
/// </remarks>
/// <param name="Name">The rule's stable name — unique within the section, the
/// <c>WorldMutation.UpsertWorldRule</c>/<c>WorldMutation.RemoveWorldRule</c> key, and the
/// <c>world.rules</c> read-back line. A <see cref="WorldCellName"/>, the same validated-identifier type a state row
/// and a cell key ride: dot-free and free of the reserved character set, refused by name (naming the offending
/// character) at the JSON converter before a document can hold one. The reserved <c>$</c> prefix is refused on top of
/// that, by <see cref="WorldRuleCompiler.CompileAll"/> — exactly as it is for a state row name, and for the same
/// reason: <c>$</c> marks what the engine mints, and nothing mints a rule.</param>
/// <param name="Effects">The effects applied in order when the rule fires.</param>
/// <param name="Gate">The predicate that must hold, or <see langword="null"/> for always.</param>
/// <param name="Mode">Whether the rule fires every tick the gate holds (<see cref="ActionTriggerMode.Level"/>, the
/// default) or once per crossing (<see cref="ActionTriggerMode.Edge"/>). A rule that writes a row almost always wants
/// <see cref="ActionTriggerMode.Edge"/>: level-firing an <c>addState</c> is what wrote 503 journal entries in 500
/// ticks.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldRule(
    WorldCellName Name,
    IReadOnlyList<ActionEffect> Effects,
    // Trails Effects and carries an explicit null default because it is genuinely optional — an always-rule omits it,
    // and the writer already omits it when null. A constructor parameter with no default is REQUIRED of a document
    // (the source-generated context enforces it), so an optional member must be able to carry one, which means
    // trailing the required ones. Document order is unaffected: JSON binds by name.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Gate = null,
    ActionTriggerMode Mode = ActionTriggerMode.Level
);
/// <summary>The reserved <see cref="ActionPredicate.CompareState"/> channels a world rule may compare against instead
/// of a declared <see cref="WorldStateRow"/> — time, population, region occupancy, a screen-machine's live memory,
/// row aggregates/extrema, spatial facts between named bodies, and a body's own reconnect-park state, all folded into
/// the same string channel <c>State</c> already carries, never a new fact enum and never a scheduler subsystem.
/// </summary>
/// <remarks>Every one of them carries the <see cref="WorldStateRow.ReservedNamePrefix"/> that no authored row name may
/// spell, so a reserved channel can never be shadowed by (or mistaken for) a real row — the validator refuses such a
/// row before a rule could ever resolve ambiguously.</remarks>
public static class WorldRuleFacts {
    /// <summary>The prefix; <c>$argmax:&lt;row&gt;</c> reads a keyed row's cells and yields the winning cell's key —
    /// not the winning value — as a body index. The genuinely new primitive: a rule can name a body. A row driving
    /// this channel is a convention, enforced at compile time (<see cref="WorldRuleRefusal.ArgRowNotKeyed"/>) and at
    /// read time (a cell whose key does not parse as a non-negative index the population actually holds is simply
    /// excluded from consideration, never a hard refusal — the same "an ineligible candidate reads as absent, not an
    /// error" posture <see cref="MachinePrefix"/>'s unbooted-machine case already sets): author a keyed row whose
    /// cell keys are body indices spelled as plain integers (<c>"0"</c>, <c>"3"</c>, …) — e.g. a per-body
    /// <c>threat</c> tally — and <c>$argmax:threat</c> is "the body with the highest tally". Ties resolve to the
    /// lowest eligible index, deterministically. An empty or entirely-ineligible row yields <c>-1</c> ("no body"),
    /// which composes with <see cref="DistancePrefix"/>/<see cref="LineOfSightPrefix"/>'s <c>argmax:&lt;row&gt;</c>
    /// body-reference token exactly as a literal <c>body:&lt;n&gt;</c> does — a spatial fact against "no body" simply
    /// never satisfies (see <c>WorldServer.ReadBodyDistance</c>'s sentinel).</summary>
    public const string ArgMaxPrefix = "$argmax:";
    /// <summary>The prefix; <c>$argmin:&lt;row&gt;</c> is <see cref="ArgMaxPrefix"/>'s dual — the body naming the
    /// smallest cell.</summary>
    public const string ArgMinPrefix = "$argmin:";
    /// <summary>The prefix; <c>$distance:&lt;bodyRefA&gt;:&lt;bodyRefB&gt;</c> reads the live straight-line distance
    /// between two named bodies — each a <c>body:&lt;n&gt;</c> literal 0-based index (the floor) or an
    /// <c>argmax:&lt;row&gt;</c>/<c>argmin:&lt;row&gt;</c> body reference (the entity-addressable widening — see
    /// <see cref="ArgMaxPrefix"/>), so <c>$distance:argmax:threat:body:3</c> gates on "how far is the highest-threat
    /// body from body 3". A within-range gate is spelled directly through the same comparand vocabulary every other
    /// operand uses — <c>compareState($distance:body:0:body:3, lessOrEqual, 5.0)</c> — rather than a second reserved
    /// channel duplicating the threshold. Either body ref resolving to no live body (an out-of-range index, an
    /// inactive slot, or an empty argmax/argmin row) reads as the engine's largest representable
    /// <see cref="Puck.Maths.FixedQ4816"/> — "infinitely far" — so a within-range gate correctly stays closed rather
    /// than spuriously opening the way a naive zero-reads-as-absent would (the opposite convention from
    /// <see cref="MachinePrefix"/>/<see cref="RegionPrefix"/>, deliberately: those measure "how much", where zero is
    /// a correct neutral count, while a spatial gate's neutral-for-absence value must never read as "close").</summary>
    public const string DistancePrefix = "$distance:";
    /// <summary>The prefix; <c>$los:&lt;bodyRefA&gt;:&lt;bodyRefB&gt;</c> reads <c>1</c> when solid world geometry
    /// leaves the sight-offset segment between two named bodies (the same body-reference grammar
    /// <see cref="DistancePrefix"/> uses) unobstructed, <c>0</c> otherwise — the same
    /// <c>Server.WorldPopulation.HasLineOfSight</c> primitive a sensed target's <c>RequiresLineOfSight</c> already
    /// rides, called directly. Either body ref resolving to no live body reads as <c>0</c> (no sight line to
    /// nothing) — the ordinary "absent reads as the falsy/neutral value" convention, unlike
    /// <see cref="DistancePrefix"/>'s deliberately-inverted sentinel (a boolean has no "too far" failure mode to
    /// guard against).</summary>
    public const string LineOfSightPrefix = "$los:";
    /// <summary>The prefix; <c>$machine:&lt;screen&gt;:&lt;address&gt;</c> compares one byte (0..255) read live off a
    /// declared <see cref="WorldScreen"/>'s booted machine — the same <c>IWorldMachineMemoryPeek.TryPeek</c> primitive
    /// <see cref="WorldAddonMemoryWatch"/> already rides, called directly instead of accumulated as a change event. A
    /// screen with no booted machine (or no memory-peek capability) reads as 0, the same "reads as zero rather than
    /// throwing" precedent a vanished state row already follows — never a hard refusal, since the machine can boot on
    /// a later tick. Deliberately fixed-width: the addon family's own shipped watch (the retired `arcade` world's
    /// win flag) was 1 byte, and a multi-byte little-endian read is not a primitive anything here has needed yet.</summary>
    public const string MachinePrefix = "$machine:";
    /// <summary>The prefix; <c>$parked:&lt;bodyRef&gt;</c> reads the remaining reconnect-grace ticks for one named
    /// body — the same body-reference grammar <see cref="DistancePrefix"/>/<see cref="LineOfSightPrefix"/> use
    /// (<c>body:&lt;n&gt;</c> or <c>argmax:&lt;row&gt;</c>/<c>argmin:&lt;row&gt;</c>), so it composes with them
    /// directly: <c>$parked:argmax:threat</c> asks "is the highest-threat body currently parked", and an authored
    /// gate can combine it with a <c>$distance:</c>/<c>$los:</c> conjunct against the same body reference in one
    /// <c>all</c> predicate. A body that is not parked, or resolves to no live body at all, reads <c>0</c> — the
    /// ordinary "absent/inapplicable reads as the neutral falsy value" convention <see cref="MachinePrefix"/>/
    /// <see cref="RegionPrefix"/> already set, appropriate here because a "parked long enough" gate
    /// (<c>compareState($parked:body:3, greaterOrEqual, 60)</c>) must stay closed for a body that was never parked,
    /// not spuriously open. See <c>Server.WorldPopulation</c>'s park-with-grace remarks for what marks a body parked
    /// and how the deadline is authored (<c>population.reconnectGraceSeconds</c>).</summary>
    public const string ParkedPrefix = "$parked:";
    /// <summary>Compares the world's own live active-population count.</summary>
    public const string Population = "$population";
    /// <summary>The prefix; <c>$reduce:&lt;op&gt;:&lt;row&gt;</c> aggregates every cell a keyed (or slot) row
    /// declares — <c>max</c>/<c>min</c>/<c>sum</c> read the row's own <see cref="CellKind"/>, <c>count</c> is always
    /// integer (the number of cells present, regardless of what they hold). The reserved-channel exemption from
    /// <see cref="WorldRuleCompiler"/>'s ordinary (row, key) pair rule: a reduction addresses the whole row rather
    /// than one cell, so it is the one place a keyed row is read with no key at all — admitted deliberately, not a
    /// hole in the pair rule (see <c>WorldRuleCompiler.ResolveOperand</c>'s reduce branch).</summary>
    public const string ReducePrefix = "$reduce:";
    /// <summary>The prefix; <c>$region:&lt;placementId&gt;</c> compares that placement's live region occupant count —
    /// the same count the world-events feed already tracks per tick, read rather than duplicated.</summary>
    /// <remarks><b>Does not collapse into <see cref="DistancePrefix"/>.</b> A <c>WorldPlacementRegion</c> is
    /// geometrically a sphere (<c>Radius</c> from the placement's own position), so "is body N inside the region"
    /// alone would in fact reduce to a distance test the distance primitive can express. The count this channel reads
    /// does not: it is an aggregate over the whole active population (however many of up to 128 bodies currently sit
    /// inside), while <see cref="DistancePrefix"/>/<see cref="LineOfSightPrefix"/> only ever name two fixed bodies —
    /// there is no "for every active body" quantifier in the rule vocabulary, and this channel's O(1) read is a cached
    /// counter <c>Server.WorldEventFeed</c> already maintains incrementally as bodies cross the boundary, never
    /// recomputed. Replacing it with the distance primitive would mean scanning up to 128 bodies' distances per rule
    /// per tick to recover a number the engine already tracks for free — a real regression for the one consumer that
    /// exists today. Kept as its own case, deliberately.</remarks>
    public const string RegionPrefix = "$region:";
    /// <summary>Compares the server's own completed-tick counter — <c>compareState("$tick", greaterOrEqual, 600)</c>
    /// is "at 2.5 seconds", with no clock read anywhere.</summary>
    public const string Tick = "$tick";
}
/// <summary>Which aggregate a <see cref="WorldRuleFacts.ReducePrefix"/> operand computes over a row's cells, or which
/// extremum a <see cref="WorldRuleFacts.ArgMaxPrefix"/>/<see cref="WorldRuleFacts.ArgMinPrefix"/> operand searches
/// for (only <see cref="Max"/>/<see cref="Min"/> are meaningful there — an arg-reduction never sums or counts).
/// </summary>
public enum WorldStateReduceOp : byte {
    /// <summary>Not a reduction (the default for every operand kind this field does not apply to).</summary>
    None,

    /// <summary>The largest cell value.</summary>
    Max,

    /// <summary>The smallest cell value.</summary>
    Min,

    /// <summary>The sum of every cell value.</summary>
    Sum,

    /// <summary>The number of cells the row declares.</summary>
    Count,
}
/// <summary>How a rule names one body — the entity-addressable primitive's own vocabulary, shared by
/// <see cref="WorldRuleFacts.DistancePrefix"/>/<see cref="WorldRuleFacts.LineOfSightPrefix"/>'s two body-reference
/// tokens.</summary>
public enum CompiledBodyRefKind : byte {
    /// <summary>A literal 0-based entity index — the floor: <c>body:&lt;n&gt;</c>.</summary>
    Literal,

    /// <summary>The body naming the largest cell of a keyed row — <c>argmax:&lt;row&gt;</c>, the same resolution
    /// <see cref="WorldRuleFacts.ArgMaxPrefix"/> performs standalone.</summary>
    ArgMax,

    /// <summary>The body naming the smallest cell of a keyed row — <c>argmin:&lt;row&gt;</c>.</summary>
    ArgMin,
}
/// <summary>One resolved body reference — see <see cref="CompiledBodyRefKind"/>.</summary>
/// <param name="Kind">How the body is named.</param>
/// <param name="Index">The literal 0-based index for <see cref="CompiledBodyRefKind.Literal"/>; unused otherwise.</param>
/// <param name="Row">The keyed row name for <see cref="CompiledBodyRefKind.ArgMax"/>/<see cref="CompiledBodyRefKind.ArgMin"/>;
/// <see langword="null"/> otherwise.</param>
public readonly record struct CompiledBodyRef(CompiledBodyRefKind Kind, int Index, string? Row);
/// <summary>What a <see cref="CompiledWorldPredicate"/> reads at evaluation time.</summary>
public enum WorldRuleFactKind : byte {
    /// <summary>A declared <see cref="WorldStateRow"/>'s named cell.</summary>
    StateCell,

    /// <summary>The server's completed-tick counter (<see cref="WorldRuleFacts.Tick"/>).</summary>
    Tick,

    /// <summary>The live active-population count (<see cref="WorldRuleFacts.Population"/>).</summary>
    Population,

    /// <summary>A named placement region's live occupant count (<see cref="WorldRuleFacts.RegionPrefix"/>).</summary>
    RegionOccupancy,

    /// <summary>One live byte off a declared screen's booted machine (<see cref="WorldRuleFacts.MachinePrefix"/>).</summary>
    MachineMemory,

    /// <summary>A numeric aggregate over a row's cells (<see cref="WorldRuleFacts.ReducePrefix"/>).</summary>
    Reduction,

    /// <summary>The body naming a row's extremal cell (<see cref="WorldRuleFacts.ArgMaxPrefix"/>/
    /// <see cref="WorldRuleFacts.ArgMinPrefix"/>) — a body-key result, not a magnitude: the value is a 0-based entity
    /// index (or <c>-1</c> for "no body"), and the two places that consume it (a spatial operand's body reference, an
    /// author's own <c>compareState</c>) read it as an address rather than a quantity. It still rides the same
    /// <see cref="Puck.Maths.FixedQ4816"/> wire <c>Server.WorldServer.ReadWorldFact</c> already returns for every
    /// other operand — the "new result type" is real at the <see cref="WorldRuleFactKind"/> level (this member
    /// distinguishes an address from a magnitude), not a second parallel read path threaded through every gate/effect
    /// site for a value that already has a canonical integer form.</summary>
    ArgBody,

    /// <summary>The live distance between two named bodies (<see cref="WorldRuleFacts.DistancePrefix"/>).</summary>
    BodyDistance,

    /// <summary>Whether two named bodies have line of sight (<see cref="WorldRuleFacts.LineOfSightPrefix"/>).</summary>
    LineOfSight,

    /// <summary>One named body's remaining reconnect-park ticks (<see cref="WorldRuleFacts.ParkedPrefix"/>).</summary>
    Parked,
}
/// <summary>One resolved operand of a world-rule comparison — the (<see cref="Kind"/>, <see cref="Row"/>,
/// <see cref="Key"/>) address plus the <see cref="Screen"/>/<see cref="Address"/> machine coordinates, the live
/// quantity <c>WorldServer.RuleGateOpen</c> reads to a <see cref="FixedQ4816"/>. Both sides of a
/// <see cref="ActionPredicate.CompareState"/> conjunct — the primary and, when spelled, the comparand — are the same
/// operand type, read by the same <c>ReadWorldFact</c> helper, so the two sides can never drift into two readings of
/// one name.</summary>
/// <param name="Kind">Which live quantity this operand reads.</param>
/// <param name="Row">The state row name for <see cref="WorldRuleFactKind.StateCell"/>, the placement id for
/// <see cref="WorldRuleFactKind.RegionOccupancy"/>, or <see langword="null"/> otherwise.</param>
/// <param name="Key">The cell key inside <paramref name="Row"/> for <see cref="WorldRuleFactKind.StateCell"/>,
/// <see langword="null"/> otherwise.</param>
/// <param name="Screen">The declared screen index for <see cref="WorldRuleFactKind.MachineMemory"/>; unused otherwise.</param>
/// <param name="Address">The machine-defined memory address for <see cref="WorldRuleFactKind.MachineMemory"/>; unused
/// otherwise.</param>
/// <param name="Reduce">The aggregate/extremum for <see cref="WorldRuleFactKind.Reduction"/> (any op) or
/// <see cref="WorldRuleFactKind.ArgBody"/> (<see cref="WorldStateReduceOp.Max"/>/<see cref="WorldStateReduceOp.Min"/>
/// only); <see cref="WorldStateReduceOp.None"/> otherwise.</param>
/// <param name="BodyA">The first named body for <see cref="WorldRuleFactKind.BodyDistance"/>/
/// <see cref="WorldRuleFactKind.LineOfSight"/>, or the one named body for <see cref="WorldRuleFactKind.Parked"/>
/// (which reads no second body); <see langword="null"/> otherwise.</param>
/// <param name="BodyB">The second named body for <see cref="WorldRuleFactKind.BodyDistance"/>/
/// <see cref="WorldRuleFactKind.LineOfSight"/>; <see langword="null"/> otherwise (including
/// <see cref="WorldRuleFactKind.Parked"/>, which is single-body).</param>
public readonly record struct CompiledWorldOperand(
    WorldRuleFactKind Kind,
    string? Row,
    string? Key,
    int Screen = 0,
    int Address = 0,
    WorldStateReduceOp Reduce = WorldStateReduceOp.None,
    CompiledBodyRef? BodyA = null,
    CompiledBodyRef? BodyB = null
);
/// <summary>One compiled, flattened conjunct of a world rule's gate — <see cref="ActionPredicate.All"/> flattens away
/// at compile time exactly as a per-body gate does, so evaluation walks one flat array with no recursion.</summary>
/// <param name="Left">The primary operand — the <c>(State, Key)</c> side of the authored <c>compareState</c>.</param>
/// <param name="Comparison">The comparison to apply.</param>
/// <param name="Value">The authored constant comparand, converted to fixed point at compile time — read only when
/// <paramref name="Comparand"/> is <see langword="null"/> (the constant spelling).</param>
/// <param name="Comparand">The comparand operand — another row/reserved channel read live on the same terms as
/// <paramref name="Left"/> (the <c>(ComparandState, ComparandKey)</c> spelling) — or <see langword="null"/> when the
/// comparand is the authored constant <paramref name="Value"/> instead.</param>
/// <param name="Describe">The authored spelling of this conjunct, for the <c>world.rules</c> read-back — an
/// <see cref="ActionPredicate.All"/> gate prints its predicates rather than a type name, which is the whole point of
/// keeping the text beside the compiled form.</param>
public readonly record struct CompiledWorldPredicate(
    CompiledWorldOperand Left,
    ActionStateComparison Comparison,
    FixedQ4816 Value,
    CompiledWorldOperand? Comparand,
    string Describe
);
/// <summary>What one compiled world-rule effect does.</summary>
public enum WorldRuleEffectKind : byte {
    /// <summary>Write a state cell (<see cref="ActionEffect.SetState"/>/<see cref="ActionEffect.AddState"/>).</summary>
    Write,

    /// <summary>Consume a non-negative integer countdown by this simulation step's engine-tick width
    /// (<see cref="ActionEffect.CountdownState"/>), saturating a final partial step at zero.</summary>
    Countdown,

    /// <summary>Fire a generator row into a text cell (<see cref="ActionEffect.Generate"/>).</summary>
    Generate,

    /// <summary>Upsert a HUD panel row (<see cref="ActionEffect.UpsertHudPanel"/>).</summary>
    UpsertHudPanel,

    /// <summary>Remove a HUD panel row (<see cref="ActionEffect.RemoveHudPanel"/>).</summary>
    RemoveHudPanel,

    /// <summary>Upsert a placement row (<see cref="ActionEffect.UpsertPlacement"/>).</summary>
    UpsertPlacement,

    /// <summary>Remove a placement row (<see cref="ActionEffect.RemovePlacement"/>).</summary>
    RemovePlacement,

    /// <summary>Write a session snapshot of the world to its own file (<see cref="ActionEffect.Save"/>) — the one
    /// kind that submits no mutation; see <see cref="ActionEffect.Save"/>'s own remarks for why.</summary>
    Save,
}
/// <summary>One compiled world-rule effect. Every kind but <see cref="WorldRuleEffectKind.Save"/> submits an ordinary
/// mutation (<c>WorldMutation.UpsertStateCell</c>, <c>WorldMutation.Generate</c>,
/// <c>WorldMutation.UpsertHudPanel</c>/<c>WorldMutation.RemoveHudPanel</c>, or
/// <c>WorldMutation.UpsertPlacement</c>/<c>WorldMutation.RemovePlacement</c>) under
/// <see cref="WorldPrincipal.World"/>, so journal and undo cover them exactly like any other write; <c>Save</c> rides
/// <c>WorldServer.FireWorldRuleEffect</c>'s own I/O tap directly instead (see <see cref="ActionEffect.Save"/>).</summary>
/// <param name="Kind">Which mutation this effect submits.</param>
/// <param name="Row">The destination state row name for <see cref="WorldRuleEffectKind.Write"/>/
/// <see cref="WorldRuleEffectKind.Generate"/>, or the panel/placement id for
/// <see cref="WorldRuleEffectKind.RemoveHudPanel"/>/<see cref="WorldRuleEffectKind.RemovePlacement"/>.</param>
/// <param name="Key">The destination cell key.</param>
/// <param name="Write">Set or add, for <see cref="WorldRuleEffectKind.Write"/>.</param>
/// <param name="RawValue">The authored constant, pre-converted to the destination row's raw encoding at compile
/// time — read only when <paramref name="From"/> is <see langword="null"/> (the literal spelling).</param>
/// <param name="Generator">The generator row name, for <see cref="WorldRuleEffectKind.Generate"/>.</param>
/// <param name="Describe">The authored spelling, for the <c>world.rules</c> read-back.</param>
/// <param name="HudPanel">The whole panel row, for <see cref="WorldRuleEffectKind.UpsertHudPanel"/>.</param>
/// <param name="Placement">The whole placement row, for <see cref="WorldRuleEffectKind.UpsertPlacement"/>.</param>
/// <param name="From">The live copy-source operand — another row/reserved channel read fresh on every firing (the
/// same <see cref="CompiledWorldOperand"/> and <c>ReadWorldFact</c> path a <see cref="CompiledWorldPredicate"/>'s
/// comparand reads through) — or <see langword="null"/> when the effect writes the authored constant
/// <paramref name="RawValue"/> instead. Applies only to <see cref="WorldRuleEffectKind.Write"/>.</param>
public readonly record struct CompiledWorldEffect(
    WorldRuleEffectKind Kind,
    string Row,
    string Key,
    WorldDocumentWriteKind Write,
    long RawValue,
    string? Generator,
    string Describe,
    WorldHudPanel? HudPanel = null,
    WorldPlacement? Placement = null,
    CompiledWorldOperand? From = null
);
/// <summary>One compiled rule: its name, its mode, the flattened gate, and the compiled effects.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="Mode">Level or edge (see <see cref="ActionTriggerMode"/>).</param>
/// <param name="Gate">The flattened conjunction; empty means "always".</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
public sealed record CompiledWorldRule(string Name, ActionTriggerMode Mode, CompiledWorldPredicate[] Gate, CompiledWorldEffect[] Effects);
/// <summary>Names why a world rule was refused during compilation. Every member is tagged
/// <see cref="RefusalAttribute"/> under the <c>world.rule.compile</c> door, so <c>world.refusals</c> lists the whole
/// family: this enum is the one exception constructor (<see cref="WorldRuleException"/>) callers pick a reason
/// from.</summary>
public enum WorldRuleRefusal : byte {
    /// <summary>The rule declares no name.</summary>
    [Refusal(door: "world.rule.compile", condition: "a rule declares no name", kind: RefusalKind.Verdict)]
    NameMissing,

    /// <summary>Another rule already declares this name.</summary>
    [Refusal(door: "world.rule.compile", condition: "another rule already declares this name", kind: RefusalKind.Verdict)]
    NameDuplicated,

    /// <summary>The rule's name carries the reserved <see cref="WorldStateRow.ReservedNamePrefix"/> prefix, which
    /// marks what the engine mints — and nothing mints a rule.</summary>
    [Refusal(door: "world.rule.compile", condition: "a rule's name carries the reserved '$' prefix, which marks what the engine mints", kind: RefusalKind.Verdict)]
    NameReserved,

    /// <summary>A predicate kind that has no world-scope meaning.</summary>
    [Refusal(door: "world.rule.compile", condition: "a gate uses a predicate kind ('now'/'recently'/'timerElapsed') that reads a per-body fact a world has none of", kind: RefusalKind.Verdict)]
    PredicateKindInadmissible,

    /// <summary>An effect kind that has no world-scope meaning.</summary>
    [Refusal(door: "world.rule.compile", condition: "an effect uses a kind that addresses a body's own kinematic/register state, which world scope has none of", kind: RefusalKind.Verdict)]
    EffectKindInadmissible,

    /// <summary>A named state row is not declared.</summary>
    [Refusal(door: "world.rule.compile", condition: "an operand names a state row the document does not declare, and is not a reserved channel", kind: RefusalKind.Verdict)]
    StateRowUnknown,

    /// <summary>A named cell is not addressable on the row named (a null key on a keyed row, or a declared row whose
    /// kind cannot carry the operation).</summary>
    [Refusal(door: "world.rule.compile", condition: "a cell is not addressable on the row named (a null key on a keyed row, a text row compared/written as a number, or a dotted 'row.key' spelling)", kind: RefusalKind.Verdict)]
    StateCellUnaddressable,

    /// <summary>A <c>$region:</c> channel names no placement carrying a region facet.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$region:<placementId>' channel names no placement carrying a region facet", kind: RefusalKind.Verdict)]
    RegionUnknown,

    /// <summary>A <c>compareState</c> names both an authored 'value' and a 'comparandState', or neither — exactly
    /// one comparand spelling is admitted (a 'comparandKey' with no 'comparandState' is refused here too).</summary>
    [Refusal(door: "world.rule.compile", condition: "a compareState names both 'value' and 'comparandState' (or neither), or a bare 'comparandKey' with no 'comparandState'", kind: RefusalKind.Verdict)]
    ComparandAmbiguous,

    /// <summary>A <c>compareState</c>'s two sides resolve to incompatible cell kinds (an <c>int</c> row against a
    /// <c>fixed</c> row, say) — mixing scales silently is refused rather than coerced.</summary>
    [Refusal(door: "world.rule.compile", condition: "a compareState's two sides resolve to incompatible cell kinds", kind: RefusalKind.Verdict)]
    ComparandKindMismatch,

    /// <summary>An effect carries an entity target, which world scope has none of.</summary>
    [Refusal(door: "world.rule.compile", condition: "an effect carries a non-Self target, which world scope has no entity to resolve", kind: RefusalKind.Verdict)]
    TargetInadmissible,

    /// <summary>A named generator row is not declared, or declares no generator.</summary>
    [Refusal(door: "world.rule.compile", condition: "a 'generate' effect names a row that is not declared, or declares no generator", kind: RefusalKind.Verdict)]
    GeneratorUnknown,

    /// <summary>A <c>$machine:</c> channel does not spell <c>$machine:&lt;screen&gt;:&lt;address&gt;</c> with
    /// non-negative integers.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$machine:' channel does not spell '$machine:<screen>:<address>' with non-negative integers", kind: RefusalKind.Verdict)]
    MachineChannelMalformed,

    /// <summary>A <c>$machine:</c> channel names a screen index the document does not declare.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$machine:' channel names a screen index the document does not declare", kind: RefusalKind.Verdict)]
    ScreenUnknown,

    /// <summary>An <c>upsertHudPanel</c>/<c>removeHudPanel</c> effect carries no panel id.</summary>
    [Refusal(door: "world.rule.compile", condition: "an 'upsertHudPanel'/'removeHudPanel' effect carries no panel id", kind: RefusalKind.Verdict)]
    HudPanelInvalid,

    /// <summary>An <c>upsertPlacement</c>/<c>removePlacement</c> effect carries no placement id.</summary>
    [Refusal(door: "world.rule.compile", condition: "an 'upsertPlacement'/'removePlacement' effect carries no placement id", kind: RefusalKind.Verdict)]
    PlacementInvalid,

    /// <summary>A <c>setState</c>/<c>addState</c> effect names both an authored 'value' and a 'fromState', or
    /// neither — exactly one write-source spelling is admitted (a 'fromKey' with no 'fromState' is refused here too),
    /// the same duality <see cref="WorldRuleRefusal.ComparandAmbiguous"/> enforces on the predicate side.</summary>
    [Refusal(door: "world.rule.compile", condition: "a setState/addState names both 'value' and 'fromState' (or neither), or a bare 'fromKey' with no 'fromState'", kind: RefusalKind.Verdict)]
    EffectSourceAmbiguous,

    /// <summary>A <c>setState</c>/<c>addState</c> effect's live <c>fromState</c> resolves to a cell kind that does
    /// not match the destination row's own kind (an <c>int</c> row fed from a <c>fixed</c> source, say) — mixing
    /// scales silently is refused rather than coerced, the effect-side sibling of
    /// <see cref="WorldRuleRefusal.ComparandKindMismatch"/>.</summary>
    [Refusal(door: "world.rule.compile", condition: "a setState/addState's live 'fromState' resolves to a cell kind that does not match the destination row's own kind", kind: RefusalKind.Verdict)]
    EffectSourceKindMismatch,

    /// <summary>A <c>setState</c>/<c>addState</c> effect's <c>valueSeconds</c> is not an exact whole engine-tick
    /// count — <see cref="Puck.Maths.FixedTickConversion.TryDurationEngineTicksExact"/> found no whole multiple of
    /// <c>1/50400</c> second equal to the authored value — this refuses rather than rounds, so a duration that
    /// silently drifted from what was authored can never happen. The message names the nearest exact
    /// durations on either side; author one of those, or author the raw engine-tick count directly via 'value' when
    /// no terminating decimal spells the intended duration exactly.</summary>
    [Refusal(door: "world.rule.compile", condition: "a setState/addState's 'valueSeconds' is not an exact whole engine-tick count (not a whole multiple of 1/50400 s), or is negative", kind: RefusalKind.Verdict)]
    DurationNotExactEngineTicks,

    /// <summary>A <c>setState</c>/<c>addState</c> effect's non-negative <c>valueSeconds</c> would compile beyond the
    /// signed 64-bit raw carrier a <c>kind=int</c> state cell stores.</summary>
    [Refusal(door: "world.rule.compile", condition: "a setState/addState's non-negative 'valueSeconds' exceeds the signed 64-bit engine-tick carrier", kind: RefusalKind.Verdict)]
    DurationEngineTicksOutOfRange,

    /// <summary>A read operand — a <c>compareState</c> subject, a <c>comparandState</c>, or a <c>fromState</c> —
    /// addresses a cell its declared row does not carry. Reading an undeclared cell would be 0 forever with no
    /// refusal anywhere, so this refuses at compile instead; the mint-later pattern declares the cell first. Write
    /// destinations are exempt — a write mints its cell, exactly as <c>world.state.cell.set</c> does.</summary>
    [Refusal(door: "world.rule.compile", condition: "a READ operand addresses a cell its declared row does not carry", kind: RefusalKind.Verdict)]
    StateCellUndeclared,

    /// <summary>A <c>$reduce:</c> channel does not spell <c>$reduce:&lt;max|min|sum|count&gt;:&lt;row&gt;</c>, or
    /// names a row that is not declared or is kind=text.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$reduce:' channel does not spell '$reduce:<max|min|sum|count>:<row>' against a declared, non-text row", kind: RefusalKind.Verdict)]
    ReduceChannelMalformed,

    /// <summary>An <c>$argmax:</c>/<c>$argmin:</c> channel names no row, or a row that is not declared or is
    /// kind=text.</summary>
    [Refusal(door: "world.rule.compile", condition: "an '$argmax:'/'$argmin:' channel names no row, or a row that is not declared or is kind=text", kind: RefusalKind.Verdict)]
    ArgChannelMalformed,

    /// <summary>An <c>$argmax:</c>/<c>$argmin:</c> channel — standalone or embedded in a
    /// <c>$distance:</c>/<c>$los:</c> body reference — names a row that is not keyed. An argmax/argmin yields a
    /// body, and a slot-shaped row's one cell carries the engine-minted <c>$value</c> key rather than a body index —
    /// author a keyed row (a per-body tally) instead.</summary>
    [Refusal(door: "world.rule.compile", condition: "an argmax/argmin body reference names a row that is not KEYED — a slot row's cell has no body-index key", kind: RefusalKind.Verdict)]
    ArgRowNotKeyed,

    /// <summary>A <c>$distance:</c>/<c>$los:</c> channel does not spell exactly two body-reference tokens
    /// (<c>body:&lt;n&gt;</c> or <c>argmax:&lt;row&gt;</c>/<c>argmin:&lt;row&gt;</c>) each.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$distance:'/'$los:' channel does not spell exactly two body-reference tokens ('body:<n>' or 'argmax:<row>'/'argmin:<row>') each", kind: RefusalKind.Verdict)]
    SpatialChannelMalformed,

    /// <summary>A <c>$parked:</c> channel does not spell exactly one body-reference token (<c>body:&lt;n&gt;</c> or
    /// <c>argmax:&lt;row&gt;</c>/<c>argmin:&lt;row&gt;</c>).</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$parked:' channel does not spell exactly one body-reference token ('body:<n>' or 'argmax:<row>'/'argmin:<row>')", kind: RefusalKind.Verdict)]
    ParkedChannelMalformed,

    /// <summary>A <c>body:&lt;n&gt;</c> reference names an index outside the document's declared entity-table
    /// capacity.</summary>
    [Refusal(door: "world.rule.compile", condition: "a 'body:<n>' reference names an index outside the document's declared entity-table capacity", kind: RefusalKind.Verdict)]
    BodyIndexUnknown,

    /// <summary>An interaction's <c>left</c>/<c>right</c> property reference names a value the declared
    /// <c>properties</c> registry does not carry — the validated-vocabulary refusal (the same shape
    /// <see cref="WorldRuleRefusal.StateRowUnknown"/> gives a bare state-row reference), catching an unknown or
    /// typo'd property name at the type rather than letting it silently compile against a same-named row that
    /// happens to exist, or never fire at all.</summary>
    [Refusal(door: "world.interaction.compile", condition: "an interaction's 'left'/'right' property reference names a value the declared 'properties' registry does not carry", kind: RefusalKind.Verdict)]
    PropertyUnknown,
}
/// <summary>Names why a world rule's effect refused to fire at runtime — distinct from <see cref="WorldRuleRefusal"/>
/// (a compile-time refusal, which stops the rule from ever installing): a runtime effect refusal is a live,
/// data-dependent decision the compiled rule could not have foreseen (whether a carrier happens to be possessed this
/// tick), so it cannot be an exception at compile time — the rule installs, and the effect is silently skipped (with
/// this reason narrated) on the tick it would otherwise fire. Tagged for <c>world.refusals</c> on the same terms as
/// <see cref="WorldRuleRefusal"/>, under its own door.</summary>
public enum WorldRuleEffectRefusal : byte {
    /// <summary>A <c>removePlacement</c> effect targets a placement whose Inhabit facet binds a live body that a
    /// concrete <c>drive</c> grant currently possesses — despawning it would silently strand that grant against a
    /// slot a later, unrelated inhabitant can claim. This refuses rather than orphans to escrow: an escrow principal
    /// is a new authority-model concept out of scope here, and refusing is the honest minimum-surface answer; the
    /// operator's remedy is explicit
    /// (<c>world.revoke &lt;principal&gt; drive body:&lt;n&gt;</c> first, or a rule that clears the possession itself
    /// before despawning).</summary>
    [Refusal(door: "world.rule.effect", condition: "a rule's 'removePlacement' effect targets a placement whose inhabited body a concrete drive grant currently possesses", kind: RefusalKind.Verdict)]
    CarrierPossessed,
}
/// <summary>Reports a world-rule (or, sharing this exact type, a world-interaction — see the constructor's own
/// <c>subject</c> parameter) compilation refusal — caught and reported by name at validation, mirroring
/// <see cref="BodyMotionProgramException"/>'s role for kit programs.</summary>
public sealed class WorldRuleException : ArgumentException {
    /// <summary>Initializes a world-rule refusal.</summary>
    /// <param name="refusal">The refusal category.</param>
    /// <param name="ruleName">The refusing rule's (or interaction's) name.</param>
    /// <param name="detail">What was wrong, in the author's own vocabulary.</param>
    /// <param name="subject">The authored-row noun this refusal names in its message — <c>"rule"</c> (the default)
    /// or <c>"interaction"</c>. An interaction desugars into, and compiles through, the same rule machinery (see
    /// <c>WorldRuleCompiler.CompileAllInteractions</c>), so this type is shared rather than forked; only the wording
    /// differs.</param>
    public WorldRuleException(WorldRuleRefusal refusal, string ruleName, string detail, string subject = "rule")
        : base(message: $"world {subject} '{ruleName}' refused {refusal}: {detail}") {
        Refusal = refusal;
    }

    /// <summary>Gets the refusal category.</summary>
    public WorldRuleRefusal Refusal { get; }
}
/// <summary>Compiles authored <see cref="WorldRule"/> rows against a candidate <see cref="WorldDefinition"/> —
/// construction at the document/mutation boundary, exactly where <c>BodyActionSpecFactory.Compile</c> sits for a kit's
/// per-body actions. Called twice by design: once (wrapped, per rule) inside <c>WorldDefinitionValidator</c> so a
/// malformed rule refuses the mutation or the boot by name instead of throwing later, and once more (unwrapped —
/// validation already proved success) inside the server's install path to obtain the live array the tick
/// evaluates.</summary>
public static class WorldRuleCompiler {
    // SetState/AddState/CountdownState/Generate lift, and — riding the SAME "admit an existing WorldMutation kind" seam Generate
    // proved — so do upsertHudPanel/removeHudPanel/upsertPlacement/removePlacement: the rest of ActionEffect writes a
    // body's own kinematic or register state, which a world rule has none of. save admits on its OWN terms — not an
    // existing mutation kind at all, but engine I/O with no document effect (see ActionEffect.Save's remarks) —
    // compiling to a fixed, argument-free CompiledWorldEffect since it addresses no row.
    private static CompiledWorldEffect CompileEffect(ActionEffect effect, string ruleName, WorldDefinition definition) => effect switch {
        ActionEffect.SetState set => ResolveWrite(
        rowName: set.State,
        key: set.Key,
        target: set.Target,
        write: WorldDocumentWriteKind.Set,
        value: set.Value,
        fromState: set.FromState,
        fromKey: set.FromKey,
        valueSeconds: set.ValueSeconds,
        ruleName: ruleName,
        definition: definition,
        verb: "setState"
    ),
        ActionEffect.AddState add => ResolveWrite(
        rowName: add.State,
        key: add.Key,
        target: add.Target,
        write: WorldDocumentWriteKind.Add,
        value: add.Value,
        fromState: add.FromState,
        fromKey: add.FromKey,
        valueSeconds: add.ValueSeconds,
        ruleName: ruleName,
        definition: definition,
        verb: "addState"
    ),
        ActionEffect.CountdownState countdown => ResolveCountdown(
        definition: definition,
        effect: countdown,
        ruleName: ruleName
    ),
        ActionEffect.Generate generate => ResolveGenerate(
        definition: definition,
        generate: generate,
        ruleName: ruleName
    ),
        ActionEffect.UpsertHudPanel upsertHud => ResolveUpsertHudPanel(
        effect: upsertHud,
        ruleName: ruleName
    ),
        ActionEffect.RemoveHudPanel removeHud => ResolveRemoveHudPanel(
        effect: removeHud,
        ruleName: ruleName
    ),
        ActionEffect.UpsertPlacement upsertPlacement => ResolveUpsertPlacement(
        effect: upsertPlacement,
        ruleName: ruleName
    ),
        ActionEffect.RemovePlacement removePlacement => ResolveRemovePlacement(
        effect: removePlacement,
        ruleName: ruleName
    ),
        ActionEffect.Save => new CompiledWorldEffect(
        Kind: WorldRuleEffectKind.Save,
        Row: string.Empty,
        Key: string.Empty,
        Write: default,
        RawValue: 0L,
        Generator: null,
        Describe: "save"
    ),
        _ => throw new WorldRuleException(
        refusal: WorldRuleRefusal.EffectKindInadmissible,
        ruleName: ruleName,
        detail: $"'{effect.GetType().Name}' has no world-scope meaning — only 'setState', 'addState', 'countdownState', 'generate', 'upsertHudPanel', 'removeHudPanel', 'upsertPlacement', 'removePlacement' and 'save' are admitted (the velocity, impulse, designate, timer and judge effects all address a body's own state)"
    ),
    };
    private static string DescribeCellKind(CellKind kind) => kind.ToString().ToLowerInvariant();
    private static string DescribeComparison(ActionStateComparison comparison) => comparison switch {
        ActionStateComparison.Equal => "==",
        ActionStateComparison.NotEqual => "!=",
        ActionStateComparison.Less => "<",
        ActionStateComparison.LessOrEqual => "<=",
        ActionStateComparison.Greater => ">",
        _ => ">=",
    };
    // Builds the world.rule.compile refusal detail for a 'valueSeconds' that is not an exact whole engine-tick
    // count — names the authored value, the arithmetic that proves it inexact, and the nearest EXACT durations on
    // either side (as engine-tick counts, which are always exact integers, plus an approximate seconds gloss for
    // orientation — 1 engine tick is 1/50400 s, which itself has no terminating decimal spelling, so the gloss is
    // never claimed exact). A negative duration is refused on separate, simpler terms: there is no "nearest exact"
    // either side of a value that is not a duration at all.
    private static string DescribeInexactDuration(string verb, string rowName, decimal literalSeconds) {
        var secondsText = literalSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);

        if (literalSeconds < 0m) {
            return $"'{verb}' authors {rowName} 'valueSeconds' {secondsText} — a duration must be non-negative.";
        }

        var scaledTicks = (literalSeconds * FixedTickConversion.TicksPerSecond);
        var lowerTicks = decimal.Floor(d: scaledTicks);
        var upperTicks = (lowerTicks + 1m);
        var lowerSeconds = (lowerTicks / FixedTickConversion.TicksPerSecond);
        var upperSeconds = (upperTicks / FixedTickConversion.TicksPerSecond);

        return ((((((string)$"'{verb}' authors {rowName} 'valueSeconds' {secondsText} — {secondsText}s * {FixedTickConversion.TicksPerSecond} engine ticks/s = {scaledTicks.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} ticks, not a whole number, so no exact engine-tick duration exists for it; ")
            + $"the nearest EXACT durations are {lowerTicks.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} engine ticks ")
            + $"(≈{lowerSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}s) and {upperTicks.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} engine ticks ")
            + $"(≈{upperSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}s) — author one of those as 'valueSeconds', or (when no terminating decimal spells the ")
            + "intended duration exactly) author the raw whole engine-tick count directly via 'value' on the row and its companion decrement rule.");
    }
    // Flattens the SAME ActionPredicate ADT a per-body gate walks: All recurses, CompareState resolves against world
    // scope, every other case is irreducibly per-body and is refused by name (never reinterpreted).
    private static void FlattenPredicate(ActionPredicate? predicate, List<CompiledWorldPredicate> gate, string ruleName, WorldDefinition definition) {
        switch (predicate) {
            case null:
                break;
            case ActionPredicate.All all:
                foreach (var inner in all.Predicates) {
                    FlattenPredicate(
                        definition: definition,
                        gate: gate,
                        predicate: inner,
                        ruleName: ruleName
                    );
                }

                break;
            case ActionPredicate.CompareState compare:
                gate.Add(item: ResolvePredicate(
                    compare: compare,
                    definition: definition,
                    ruleName: ruleName
                ));

                break;
            default:
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.PredicateKindInadmissible,
                    ruleName: ruleName,
                    detail: $"'{predicate.GetType().Name}' has no world-scope meaning — only 'compareState' and 'all' are admitted ('now'/'recently' read a per-body engine fact and 'timerElapsed' reads a per-body timer slot; a world has neither)"
                );
        }
    }
    private static bool HasRegion(WorldDefinition definition, string placementId) {
        foreach (var placement in definition.Placements) {
            if (
                (placement.Region is not null) &&
                string.Equals(
                a: placement.Id,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return true;
            }
        }

        return false;
    }
    // Structural only — whether the screen index a $machine: channel names is DECLARED, mirroring HasRegion's own
    // minimal bar. Which SOURCE the screen carries (machine vs. camera vs. view) is not checked here: a screen can be
    // re-sourced live, and a screen with no booted machine simply reads as 0 at evaluation time (WorldServer.Machines
    // .TryPeek), the same "reads as zero rather than throwing" precedent ReadStateCell already follows.
    private static bool HasScreen(WorldDefinition definition, int index) {
        foreach (var screen in definition.Screens) {
            if (screen.Index == index) {
                return true;
            }
        }

        return false;
    }
    private static void RefuseKeyOnReservedChannel(string? key, string ruleName, string name, string keyFieldLabel) {
        if (key is not null) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"reserved channel '{name}' is a single quantity and carries no cells — drop the '{keyFieldLabel}'"
            );
        }
    }
    // Parses ONE body-reference token pair (tokens[start], tokens[start+1]) — "body:<n>" (a literal 0-based index,
    // bounded against the document's OWN declared entity-table capacity) or "argmax:<row>"/"argmin:<row>" (a
    // reduction-derived body key, resolved through the SAME ResolveNumericRow(requireKeyed: true) door the standalone
    // $argmax:/$argmin: channel uses) — the shared grammar $distance:/$los: spend both their halves on.
    private static CompiledBodyRef ResolveBodyRefToken(string[] tokens, int start, string ruleName, WorldDefinition definition, string channel) {
        var kind = tokens[start];
        var value = tokens[(start + 1)];

        if (string.Equals(
            a: kind,
            b: "body",
            comparisonType: StringComparison.Ordinal
        )) {
            if (
                !int.TryParse(
                s: value,
                style: System.Globalization.NumberStyles.Integer,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var index
            ) ||
                (index < 0)
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{channel}' names 'body:{value}', which is not a non-negative integer"
                );
            }

            if (index >= definition.Population.Capacity) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.BodyIndexUnknown,
                    ruleName: ruleName,
                    detail: $"'{channel}' names 'body:{index}', which is outside the document's declared entity-table capacity ({definition.Population.Capacity})"
                );
            }

            return new CompiledBodyRef(
                Index: index,
                Kind: CompiledBodyRefKind.Literal,
                Row: null
            );
        }

        if (
            string.Equals(
            a: kind,
            b: "argmax",
            comparisonType: StringComparison.Ordinal
        ) ||
            string.Equals(
            a: kind,
            b: "argmin",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            if (string.IsNullOrEmpty(value: value)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{channel}' names '{kind}:' with no row"
                );
            }

            _ = ResolveNumericRow(
                channel: channel,
                definition: definition,
                malformed: WorldRuleRefusal.SpatialChannelMalformed,
                name: value,
                requireKeyed: true,
                ruleName: ruleName
            );

            return new CompiledBodyRef(
                Index: -1,
                Kind: ((kind == "argmax")
                ? CompiledBodyRefKind.ArgMax
                : CompiledBodyRefKind.ArgMin),
                Row: value
            );
        }

        throw new WorldRuleException(
            refusal: WorldRuleRefusal.SpatialChannelMalformed,
            ruleName: ruleName,
            detail: $"'{channel}' names body-reference token '{kind}:{value}' — expected 'body:<n>' or 'argmax:<row>'/'argmin:<row>'"
        );
    }
    private static CompiledWorldEffect ResolveCountdown(ActionEffect.CountdownState effect, string ruleName, WorldDefinition definition) {
        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: effect.State
        )
            ?? throw new WorldRuleException(
            refusal: WorldRuleRefusal.StateRowUnknown,
            ruleName: ruleName,
            detail: $"'countdownState' names no state row '{effect.State}' — declare it with world.row.set state <json> first"
        ));

        if (
            (row.Kind != CellKind.Int) ||
            !row.NonNegative
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"state row '{effect.State}' is kind={DescribeCellKind(kind: row.Kind)} nonNegative={row.NonNegative.ToString().ToLowerInvariant()} — 'countdownState' requires kind=int nonNegative=true so its computed final partial step can saturate at zero"
            );
        }

        var resolvedKey = ResolveKey(
            row: row,
            key: effect.Key,
            ruleName: ruleName,
            verb: "countdownState",
            keyFieldLabel: "key"
        );

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.Countdown,
            Row: effect.State,
            Key: resolvedKey,
            Write: WorldDocumentWriteKind.Add,
            RawValue: 0L,
            Generator: null,
            Describe: $"countdownState {effect.State}.{resolvedKey} by runtime step"
        );
    }
    // A 'generate' effect names ONE thing: the SITE to redraw. The source is the site's own facet (named or
    // inlined), so there is no second row to resolve and no key to address — a draw site is a scalar slot by
    // construction. Timing is the one refusal this compile adds: a boot-timed site draws once at first fill and can
    // never be redrawn, and an author sees that here rather than at the first tick the rule fires.
    private static CompiledWorldEffect ResolveGenerate(ActionEffect.Generate generate, string ruleName, WorldDefinition definition) {
        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: generate.Row
        )
            ?? throw new WorldRuleException(
            refusal: WorldRuleRefusal.StateRowUnknown,
            ruleName: ruleName,
            detail: $"'generate' names no state row '{generate.Row}'"
        ));

        if (row.Draw is not { } draw) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.GeneratorUnknown,
                ruleName: ruleName,
                detail: $"state row '{generate.Row}' declares no draw — 'generate' redraws a draw site"
            );
        }

        if (draw.Timing == WorldDrawTiming.Boot) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.GeneratorUnknown,
                ruleName: ruleName,
                detail: $"state row '{generate.Row}' declares timing=boot — it draws once at first fill and is never redrawn"
            );
        }

        if (!WorldGeneratorEngine.TryResolveSource(
            generators: definition.Generators,
            draw: draw,
            generator: out var generator,
            reason: out var resolveReason
        )) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.GeneratorUnknown,
                ruleName: ruleName,
                detail: $"state row '{generate.Row}' {resolveReason}"
            );
        }

        // The ONE kind predicate, asked here at rule COMPILE time so an author sees a mismatch before the effect ever
        // fires — the same call the fire-time door makes, never a second reading of it.
        if (!WorldGeneratorEngine.TryCheckTargetKind(
            source: generator.Source,
            targetKind: row.Kind,
            reason: out var kindReason
        )) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"state row '{generate.Row}': {kindReason}"
            );
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.Generate,
            Row: generate.Row,
            Key: WorldStateRow.SlotKey,
            Write: WorldDocumentWriteKind.Set,
            RawValue: 0L,
            Generator: generate.Row,
            Describe: $"generate {generate.Row}"
        );
    }
    // The (row, key) PAIR rule: a null key means the row's slot cell, and WorldStateRow.IsKeyed — never "declares a
    // capacity", and never !IsSlot — is the discriminator (a capacity-free row carrying several author-keyed cells
    // has no slot either, while a row with NO cells is legitimately slot-addressable: the first write mints its slot
    // cell, exactly as world.state.cell.set does).
    private static string ResolveKey(WorldStateRow row, string? key, string ruleName, string verb, string keyFieldLabel) {
        if (key is { } authored) {
            return (WorldCellName.TryParse(
                candidate: authored,
                name: out var parsed,
                reason: out var reason
            )
                ? parsed.Value
                : throw new WorldRuleException(
                    refusal: WorldRuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"'{verb}' {keyFieldLabel} '{authored}' {reason}"
                )
            );
        }

        return (row.IsKeyed
            ? throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{verb}' names keyed row '{row.Name}' without a '{keyFieldLabel}' — a keyed row has no single cell, so name the one you mean"
            )
            : WorldStateRow.SlotKey.Value
        );
    }
    // The declared-row resolution a $reduce:/$argmax:/$argmin: channel shares: the row must exist and must not be
    // kind=text (a reduction/extremum is numeric, exactly like the ordinary declared-row path below). requireKeyed
    // additionally demands the row be POSITIVELY keyed (WorldStateRow.IsKeyed) — an argmax/argmin yields a BODY, and
    // a slot row's one cell carries the engine-minted $value key rather than a body index, so a slot row is refused
    // there (ArgRowNotKeyed) but admitted for an ordinary reduction (a slot row's max/min/sum trivially equals its
    // one cell; count is 1).
    private static WorldStateRow ResolveNumericRow(string name, string ruleName, WorldDefinition definition, bool requireKeyed, WorldRuleRefusal malformed, string channel) {
        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: name
        )
            ?? throw new WorldRuleException(
            refusal: malformed,
            ruleName: ruleName,
            detail: $"'{channel}' names row '{name}', which the document does not declare"
        ));

        if (row.Kind == CellKind.Text) {
            throw new WorldRuleException(
                refusal: malformed,
                ruleName: ruleName,
                detail: $"'{channel}' names row '{name}', which is kind=text — a reduction/extremum is numeric, never text"
            );
        }

        if (
            requireKeyed &&
            !row.IsKeyed
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.ArgRowNotKeyed,
                ruleName: ruleName,
                detail: $"'{channel}' names row '{name}', which is not keyed — an argmax/argmin yields a body, and a slot row's cell carries no body-index key; author a keyed row whose cell keys ARE body indices"
            );
        }

        return row;
    }
    // Resolves ANY read operand — a compareState's primary (State, Key) pair, its comparand (ComparandState,
    // ComparandKey) pair, or a setState/addState's live copy source (FromState, FromKey) — through the SAME
    // reserved-channel/state-row walk, so no two of them can drift into different readings of the same name.
    // verb/fieldLabel/keyFieldLabel name the AUTHORED spelling in refusal text, since every caller is refused by the
    // same shapes under different-sounding names ("state"/"key" vs "comparandState"/"comparandKey" vs "fromState"/
    // "fromKey"): a refusal that quoted one caller's spelling at another's author would name a field they never wrote.
    // Reserved channels ($tick/$population/$region:/$machine:) are all integer-valued; a declared row carries its own
    // kind. The $machine: channel is resolved here too, so every caller reaches a live machine byte on the same terms.
    private static ResolvedOperand ResolveOperand(string name, string? key, string ruleName, WorldDefinition definition, string verb, string fieldLabel, string keyFieldLabel) {
        var describe = $"{name}{((key is { } spelledKey)
            ? $".{spelledKey}"
            : string.Empty)}";

        if (string.Equals(
            a: name,
            b: WorldRuleFacts.Tick,
            comparisonType: StringComparison.Ordinal
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(
                    Kind: WorldRuleFactKind.Tick,
                    Row: null,
                    Key: null
                ),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (string.Equals(
            a: name,
            b: WorldRuleFacts.Population,
            comparisonType: StringComparison.Ordinal
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(
                    Kind: WorldRuleFactKind.Population,
                    Row: null,
                    Key: null
                ),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.RegionPrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var placementId = name[WorldRuleFacts.RegionPrefix.Length..];

            if (
                string.IsNullOrEmpty(value: placementId) ||
                !HasRegion(
                definition: definition,
                placementId: placementId
            )
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.RegionUnknown,
                    ruleName: ruleName,
                    detail: $"'{name}' names no placement carrying a region facet"
                );
            }

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(
                    Kind: WorldRuleFactKind.RegionOccupancy,
                    Row: placementId,
                    Key: null
                ),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.MachinePrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var suffix = name[WorldRuleFacts.MachinePrefix.Length..];
            var separator = suffix.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: ':'
            );

            if (
                (separator < 0) ||
                !int.TryParse(
                s: suffix[..separator],
                style: System.Globalization.NumberStyles.Integer,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var screen
            ) ||
                !int.TryParse(
                s: suffix[(separator + 1)..],
                style: System.Globalization.NumberStyles.Integer,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var address
            ) ||
                (screen < 0) ||
                (address < 0)
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.MachineChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.MachinePrefix}<screen>:<address>' with non-negative integers"
                );
            }

            if (!HasScreen(
                definition: definition,
                index: screen
            )) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ScreenUnknown,
                    ruleName: ruleName,
                    detail: $"'{name}' names screen {screen}, which the document does not declare"
                );
            }

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(
                    Kind: WorldRuleFactKind.MachineMemory,
                    Row: null,
                    Key: null,
                    Screen: screen,
                    Address: address
                ),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.ReducePrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var suffix = name[WorldRuleFacts.ReducePrefix.Length..];
            var separator = suffix.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: ':'
            );

            if (
                (separator < 0) ||
                !TryParseReduceOp(
                text: suffix[..separator],
                op: out var op
            ) ||
                string.IsNullOrEmpty(value: suffix[(separator + 1)..])
            ) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ReduceChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.ReducePrefix}<max|min|sum|count>:<row>'"
                );
            }

            var rowName = suffix[(separator + 1)..];
            var reduceRow = ResolveNumericRow(
                channel: name,
                definition: definition,
                malformed: WorldRuleRefusal.ReduceChannelMalformed,
                name: rowName,
                requireKeyed: false,
                ruleName: ruleName
            );
            var reduceValueKind = ((op == WorldStateReduceOp.Count)
                ? CellKind.Int
                : reduceRow.Kind
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(
                    Kind: WorldRuleFactKind.Reduction,
                    Row: rowName,
                    Key: null,
                    Reduce: op
                ),
                ValueKind: reduceValueKind,
                Describe: describe
            );
        }

        if (
            name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.ArgMaxPrefix
        ) ||
            name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.ArgMinPrefix
        )
        ) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var isMax = name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.ArgMaxPrefix
            );
            var rowName = name[(isMax
                ? WorldRuleFacts.ArgMaxPrefix.Length
                : WorldRuleFacts.ArgMinPrefix.Length)..];

            if (string.IsNullOrEmpty(value: rowName)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ArgChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{(isMax
                    ? WorldRuleFacts.ArgMaxPrefix
                    : WorldRuleFacts.ArgMinPrefix)}<row>'"
                );
            }

            _ = ResolveNumericRow(
                channel: name,
                definition: definition,
                malformed: WorldRuleRefusal.ArgChannelMalformed,
                name: rowName,
                requireKeyed: true,
                ruleName: ruleName
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(
                    Kind: WorldRuleFactKind.ArgBody,
                    Row: rowName,
                    Key: null,
                    Reduce: (isMax
                ? WorldStateReduceOp.Max
                : WorldStateReduceOp.Min)
                ),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (
            name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.DistancePrefix
        ) ||
            name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.LineOfSightPrefix
        )
        ) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var isDistance = name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.DistancePrefix
            );
            var suffix = name[(isDistance
                ? WorldRuleFacts.DistancePrefix.Length
                : WorldRuleFacts.LineOfSightPrefix.Length)..];
            var tokens = suffix.Split(separator: ':');

            if (tokens.Length != 4) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.SpatialChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{(isDistance
                    ? WorldRuleFacts.DistancePrefix
                    : WorldRuleFacts.LineOfSightPrefix)}<bodyRefA>:<bodyRefB>' (each a 'body:<n>' or 'argmax:<row>'/'argmin:<row>' pair)"
                );
            }

            var bodyA = ResolveBodyRefToken(
                channel: name,
                definition: definition,
                ruleName: ruleName,
                start: 0,
                tokens: tokens
            );
            var bodyB = ResolveBodyRefToken(
                channel: name,
                definition: definition,
                ruleName: ruleName,
                start: 2,
                tokens: tokens
            );
            var spatialKind = (isDistance
                ? WorldRuleFactKind.BodyDistance
                : WorldRuleFactKind.LineOfSight
            );
            var spatialValueKind = (isDistance
                ? CellKind.Fixed
                : CellKind.Bool
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(
                    Kind: spatialKind,
                    Row: null,
                    Key: null,
                    BodyA: bodyA,
                    BodyB: bodyB
                ),
                ValueKind: spatialValueKind,
                Describe: describe
            );
        }

        // $parked: — the SAME single-body-reference grammar $distance:/$los: spend one half of theirs on, so it
        // composes with argmax/argmin directly ($parked:argmax:threat).
        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldRuleFacts.ParkedPrefix
        )) {
            RefuseKeyOnReservedChannel(
                key: key,
                keyFieldLabel: keyFieldLabel,
                name: name,
                ruleName: ruleName
            );

            var suffix = name[WorldRuleFacts.ParkedPrefix.Length..];
            var tokens = suffix.Split(separator: ':');

            if (tokens.Length != 2) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.ParkedChannelMalformed,
                    ruleName: ruleName,
                    detail: $"'{name}' does not spell '{WorldRuleFacts.ParkedPrefix}<bodyRef>' (a 'body:<n>' or 'argmax:<row>'/'argmin:<row>' pair)"
                );
            }

            var parkedBody = ResolveBodyRefToken(
                channel: name,
                definition: definition,
                ruleName: ruleName,
                start: 0,
                tokens: tokens
            );

            return new ResolvedOperand(
                Operand: new CompiledWorldOperand(
                    Kind: WorldRuleFactKind.Parked,
                    Row: null,
                    Key: null,
                    BodyA: parkedBody
                ),
                ValueKind: CellKind.Int,
                Describe: describe
            );
        }

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldStateRow.ReservedNamePrefix
        )) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"'{name}' carries the reserved '{WorldStateRow.ReservedNamePrefix}' prefix but names none of the reserved channels ('{WorldRuleFacts.Tick}', '{WorldRuleFacts.Population}', '{WorldRuleFacts.RegionPrefix}<placementId>', '{WorldRuleFacts.MachinePrefix}<screen>:<address>', '{WorldRuleFacts.ReducePrefix}<op>:<row>', '{WorldRuleFacts.ArgMaxPrefix}<row>', '{WorldRuleFacts.ArgMinPrefix}<row>', '{WorldRuleFacts.DistancePrefix}<a>:<b>', '{WorldRuleFacts.LineOfSightPrefix}<a>:<b>', '{WorldRuleFacts.ParkedPrefix}<bodyRef>')"
            );
        }

        // A declared row name is dot-free by construction (WorldCellName refuses a dot) — this only ever fires for an
        // author reaching for a "row.key" spelling in one string. Named explicitly rather than falling through to a
        // generic "unknown row", which would leave the actual mistake (use the separate key field) unsaid.
        if (name.Contains(value: '.')) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{fieldLabel}' value '{name}' carries a '.' — a state row name is never dotted; address the cell with '{keyFieldLabel}' instead of dotting it into '{fieldLabel}'"
            );
        }

        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: name
        )
            ?? throw new WorldRuleException(
            refusal: WorldRuleRefusal.StateRowUnknown,
            ruleName: ruleName,
            detail: $"'{name}' names no state row, and is not a reserved channel ('{WorldRuleFacts.Tick}', '{WorldRuleFacts.Population}', '{WorldRuleFacts.RegionPrefix}<placementId>', '{WorldRuleFacts.MachinePrefix}<screen>:<address>', '{WorldRuleFacts.ReducePrefix}<op>:<row>', '{WorldRuleFacts.ArgMaxPrefix}<row>', '{WorldRuleFacts.ArgMinPrefix}<row>', '{WorldRuleFacts.DistancePrefix}<a>:<b>', '{WorldRuleFacts.LineOfSightPrefix}<a>:<b>', '{WorldRuleFacts.ParkedPrefix}<bodyRef>')"
        ));

        if (row.Kind == CellKind.Text) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"state row '{name}' is kind=text — a rule compares numbers, never text"
            );
        }

        var resolvedKey = ResolveKey(
            key: key,
            keyFieldLabel: keyFieldLabel,
            row: row,
            ruleName: ruleName,
            verb: verb
        );

        // A READ operand must address a cell the row declares TODAY: an undeclared cell reads 0 forever with no
        // refusal anywhere (silently broken gating), so it refuses at compile instead. Write destinations mint their
        // cells and are deliberately not funneled through here.
        //
        // A DRAW SITE's slot cell is DECLARED BY ITS FACET, even before it holds one: the boot resolver fills every
        // first-fill site at load, so a running document's draw site always carries its cell. Validation runs on the
        // document BEFORE that resolution, so without this arm a rule gated on a drawn value would refuse at boot for
        // a document that is correct — the refusal would report a state the engine passes through, never one it runs
        // in.
        if (
            !row.HasCell(key: resolvedKey) &&
            !(row.IsDraw && (resolvedKey == WorldStateRow.SlotKey.Value))
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUndeclared,
                ruleName: ruleName,
                detail: $"'{verb}' {fieldLabel} '{name}' reads cell '{resolvedKey}', which the row does not declare — an undeclared cell reads 0 forever; declare the cell first (an authored 0 is fine)"
            );
        }

        return new ResolvedOperand(
            Operand: new CompiledWorldOperand(
                Kind: WorldRuleFactKind.StateCell,
                Row: name,
                Key: resolvedKey
            ),
            ValueKind: row.Kind,
            Describe: describe
        );
    }
    private static CompiledWorldPredicate ResolvePredicate(ActionPredicate.CompareState compare, string ruleName, WorldDefinition definition) {
        var name = (compare.State ?? string.Empty);
        var comparison = compare.Comparison;
        var hasValue = (compare.Value is not null);
        var hasComparand = (compare.ComparandState is not null);

        // 'comparandKey' is an appendage of 'comparandState'; on its own it is a parsed-and-discarded field, refused
        // by name rather than silently ignored under the constant spelling.
        if (
            (compare.ComparandKey is not null) &&
            (compare.ComparandState is null)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.ComparandAmbiguous,
                ruleName: ruleName,
                detail: "names 'comparandKey' without 'comparandState' — a comparand key addresses a cell inside a comparand row, which must be named"
            );
        }

        // Exactly one comparand spelling: an authored constant, or another row/channel read live. Both, or neither,
        // is an authoring mistake refused by name rather than one spelling silently winning.
        if (hasValue == hasComparand) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.ComparandAmbiguous,
                ruleName: ruleName,
                detail: (hasValue
                ? "names both 'value' and 'comparandState' — a compareState spells exactly one comparand, never both"
                : "names neither 'value' nor 'comparandState' — a compareState must spell exactly one comparand")
            );
        }

        var lhs = ResolveOperand(
            name: name,
            key: compare.Key,
            ruleName: ruleName,
            definition: definition,
            verb: "compareState",
            fieldLabel: "state",
            keyFieldLabel: "key"
        );

        if (hasValue) {
            var value = FixedQ4816.FromDouble(value: compare.Value!.Value);
            var describe = $"{lhs.Describe} {DescribeComparison(comparison: comparison)} {compare.Value.Value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}";

            return new CompiledWorldPredicate(
                Left: lhs.Operand,
                Comparison: comparison,
                Value: value,
                Comparand: null,
                Describe: describe
            );
        }

        var rhs = ResolveOperand(
            name: compare.ComparandState!,
            key: compare.ComparandKey,
            ruleName: ruleName,
            definition: definition,
            verb: "compareState",
            fieldLabel: "comparandState",
            keyFieldLabel: "comparandKey"
        );

        // Mixed kinds refuse by name: an int tick count against a fixed-point row (or vice versa) mixes scales
        // silently, which is worse than naming the mismatch — the constant spelling keeps its existing, more
        // permissive behavior (every shipped world's compareState already leans on it), so this check applies ONLY
        // to the new comparand-row spelling.
        if (lhs.ValueKind != rhs.ValueKind) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.ComparandKindMismatch,
                ruleName: ruleName,
                detail: $"'{name}' is kind={DescribeCellKind(kind: lhs.ValueKind)} but comparand '{compare.ComparandState}' is kind={DescribeCellKind(kind: rhs.ValueKind)} — mixed-kind comparisons are refused; author both sides the same kind"
            );
        }

        var mixedDescribe = $"{lhs.Describe} {DescribeComparison(comparison: comparison)} {rhs.Describe}";

        return new CompiledWorldPredicate(
            Left: lhs.Operand,
            Comparison: comparison,
            Value: default,
            Comparand: rhs.Operand,
            Describe: mixedDescribe
        );
    }
    private static CompiledWorldEffect ResolveRemoveHudPanel(ActionEffect.RemoveHudPanel effect, string ruleName) {
        if (string.IsNullOrWhiteSpace(value: effect.Id)) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.HudPanelInvalid,
                ruleName: ruleName,
                detail: "'removeHudPanel' names no panel 'id'"
            );
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.RemoveHudPanel,
            Row: effect.Id,
            Key: string.Empty,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"removeHudPanel {effect.Id}"
        );
    }
    private static CompiledWorldEffect ResolveRemovePlacement(ActionEffect.RemovePlacement effect, string ruleName) {
        if (string.IsNullOrWhiteSpace(value: effect.Id)) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.PlacementInvalid,
                ruleName: ruleName,
                detail: "'removePlacement' names no placement 'id'"
            );
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.RemovePlacement,
            Row: effect.Id,
            Key: string.Empty,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"removePlacement {effect.Id}"
        );
    }
    // upsertHudPanel/upsertPlacement are whole-row upserts, exactly like WorldMutation.UpsertHudPanel/UpsertPlacement
    // submitted from the console or an addon — the row's own content (capacity, unknown binding, unresolved
    // creationId) is validated by the ORDINARY whole-document revalidation when the effect actually fires, never
    // duplicated here. Compile time checks only what a whole-row upsert can check in isolation: that it names itself.
    private static CompiledWorldEffect ResolveUpsertHudPanel(ActionEffect.UpsertHudPanel effect, string ruleName) {
        if (
            (effect.Panel is null) ||
            string.IsNullOrWhiteSpace(value: effect.Panel.Id)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.HudPanelInvalid,
                ruleName: ruleName,
                detail: "'upsertHudPanel' names no panel 'id'"
            );
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.UpsertHudPanel,
            Row: effect.Panel.Id,
            Key: string.Empty,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"upsertHudPanel {effect.Panel.Id}",
            HudPanel: effect.Panel
        );
    }
    private static CompiledWorldEffect ResolveUpsertPlacement(ActionEffect.UpsertPlacement effect, string ruleName) {
        if (
            (effect.Placement is null) ||
            string.IsNullOrWhiteSpace(value: effect.Placement.Id)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.PlacementInvalid,
                ruleName: ruleName,
                detail: "'upsertPlacement' names no placement 'id'"
            );
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.UpsertPlacement,
            Row: effect.Placement.Id,
            Key: string.Empty,
            Write: default,
            RawValue: 0L,
            Generator: null,
            Describe: $"upsertPlacement {effect.Placement.Id}",
            Placement: effect.Placement
        );
    }
    // value XOR valueSeconds XOR (fromState, fromKey): the SAME duality ResolvePredicate enforces for compareState's
    // comparand, applied to the write side and widened by one spelling. 'fromKey' is an appendage of 'fromState' on
    // the same terms 'comparandKey' is.
    private static CompiledWorldEffect ResolveWrite(string rowName, string? key, ActionTarget target, WorldDocumentWriteKind write, float? value, string? fromState, string? fromKey, decimal? valueSeconds, string ruleName, WorldDefinition definition, string verb) {
        if (target != ActionTarget.Self) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.TargetInadmissible,
                ruleName: ruleName,
                detail: $"'{verb}' carries target '{target}' — a world rule has no entity to address, so a target is refused rather than parsed and discarded"
            );
        }

        var row = (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: rowName
        )
            ?? throw new WorldRuleException(
            refusal: WorldRuleRefusal.StateRowUnknown,
            ruleName: ruleName,
            detail: $"'{verb}' names no state row '{rowName}' — declare it with world.row.set state <json> first"
        ));

        if (row.Kind == CellKind.Text) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"state row '{rowName}' is kind=text — '{verb}' writes a number"
            );
        }

        var resolvedKey = ResolveKey(
            key: key,
            keyFieldLabel: "key",
            row: row,
            ruleName: ruleName,
            verb: verb
        );
        var hasValue = (value is not null);
        var hasFrom = (fromState is not null);
        var hasValueSeconds = (valueSeconds is not null);

        if (
            (fromKey is not null) &&
            (fromState is null)
        ) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"'{verb}' names 'fromKey' without 'fromState' — a copy source key addresses a cell inside a source row, which must be named"
            );
        }

        var spellingCount = (((hasValue
            ? 1
            : 0) + (hasFrom
            ? 1
            : 0)) + (hasValueSeconds
            ? 1
            : 0));

        if (spellingCount != 1) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"'{verb}' must name EXACTLY ONE of 'value', 'valueSeconds', or 'fromState' — named {spellingCount}"
            );
        }

        if (hasValueSeconds) {
            if (row.Kind != CellKind.Int) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} — '{verb}' 'valueSeconds' authors a whole engine-tick countdown, meaningful only against a kind=int row"
                );
            }

            var literalSeconds = valueSeconds!.Value;
            var maximumSeconds = (((decimal)long.MaxValue) / FixedTickConversion.TicksPerSecond);

            if (literalSeconds > maximumSeconds) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.DurationEngineTicksOutOfRange,
                    ruleName: ruleName,
                    detail: $"'{verb}' authors {rowName} 'valueSeconds' {literalSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} — the duration exceeds the signed 64-bit state carrier's maximum of {long.MaxValue} engine ticks (approximately {maximumSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} seconds)"
                );
            }

            if (!FixedTickConversion.TryDurationEngineTicksExact(
                seconds: literalSeconds,
                ticks: out var ticks
            )) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.DurationNotExactEngineTicks,
                    ruleName: ruleName,
                    detail: DescribeInexactDuration(
                        literalSeconds: literalSeconds,
                        rowName: rowName,
                        verb: verb
                    )
                );
            }

            return new CompiledWorldEffect(
                Kind: WorldRuleEffectKind.Write,
                Row: rowName,
                Key: resolvedKey,
                Write: write,
                RawValue: checked((long)ticks),
                Generator: null,
                Describe: $"{verb} {rowName}.{resolvedKey} = {literalSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}s ({ticks} engine ticks)"
            );
        }

        if (hasValue) {
            var literal = value!.Value;
            var raw = row.Kind switch {
                CellKind.Int => checked((long)MathF.Round(x: literal)),
                CellKind.Fixed => FixedQ4816.FromDouble(value: literal).Value,
                _ => ((literal != 0f)
                ? 1L
                : 0L), // Bool — Text already refused above.
            };

            return new CompiledWorldEffect(
                Kind: WorldRuleEffectKind.Write,
                Row: rowName,
                Key: resolvedKey,
                Write: write,
                RawValue: raw,
                Generator: null,
                Describe: $"{verb} {rowName}.{resolvedKey} = {literal.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}"
            );
        }

        var source = ResolveOperand(
            definition: definition,
            fieldLabel: "fromState",
            key: fromKey,
            keyFieldLabel: "fromKey",
            name: fromState!,
            ruleName: ruleName,
            verb: verb
        );

        if (source.ValueKind != row.Kind) {
            throw new WorldRuleException(
                refusal: WorldRuleRefusal.EffectSourceKindMismatch,
                ruleName: ruleName,
                detail: $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} but 'fromState' '{fromState}' is kind={DescribeCellKind(kind: source.ValueKind)} — mixed-kind copies are refused; author both sides the same kind"
            );
        }

        return new CompiledWorldEffect(
            Kind: WorldRuleEffectKind.Write,
            Row: rowName,
            Key: resolvedKey,
            Write: write,
            RawValue: 0L,
            Generator: null,
            Describe: $"{verb} {rowName}.{resolvedKey} := {source.Describe}",
            From: source.Operand
        );
    }
    private static bool TryParseReduceOp(string text, out WorldStateReduceOp op) {
        op = text switch {
            "max" => WorldStateReduceOp.Max,
            "min" => WorldStateReduceOp.Min,
            "sum" => WorldStateReduceOp.Sum,
            "count" => WorldStateReduceOp.Count,
            _ => WorldStateReduceOp.None,
        };

        return (op != WorldStateReduceOp.None);
    }

    /// <summary>Compiles one rule against a candidate document. Does not check name presence or uniqueness — that is
    /// <see cref="CompileAll"/>'s job, the one caller with a sibling list to check against.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="definition">The candidate document.</param>
    /// <returns>The compiled rule.</returns>
    /// <exception cref="WorldRuleException">The rule names something the document does not declare, or uses a
    /// predicate/effect kind world scope has no meaning for.</exception>
    public static CompiledWorldRule Compile(WorldRule rule, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: definition);

        var gate = new List<CompiledWorldPredicate>();

        FlattenPredicate(
            predicate: rule.Gate,
            gate: gate,
            ruleName: rule.Name,
            definition: definition
        );

        var effects = new CompiledWorldEffect[rule.Effects.Count];

        for (var index = 0; (index < effects.Length); index++) {
            effects[index] = CompileEffect(
                effect: rule.Effects[index],
                ruleName: rule.Name,
                definition: definition
            );
        }

        return new CompiledWorldRule(
            Name: rule.Name,
            Mode: rule.Mode,
            Gate: gate.ToArray(),
            Effects: effects
        );
    }
    /// <summary>Compiles every rule in the definition's <c>rules</c> section, in document order.</summary>
    /// <param name="definition">The candidate document — its <c>state</c> and <c>placements</c> sections resolve
    /// every name a rule can spell.</param>
    /// <returns>The compiled rules, in authored order.</returns>
    /// <exception cref="WorldRuleException">A rule's name is missing, reserved, or duplicated, or it fails to
    /// compile.</exception>
    public static CompiledWorldRule[] CompileAll(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rules = (definition.Rules ?? []);

        if (rules.Count == 0) {
            return [];
        }

        var seen = new HashSet<string>(
            capacity: rules.Count,
            comparer: StringComparer.Ordinal
        );
        var compiled = new CompiledWorldRule[rules.Count];

        for (var index = 0; (index < rules.Count); index++) {
            var rule = rules[index];
            // WorldCellName already proved the shape (non-empty, dot-free, free of the reserved character set) at the
            // JSON converter or at the console verb, naming the offending character — a default-valued struct from a
            // programmatically built definition is the one way an empty name still reaches here.
            var name = (rule?.Name.Value ?? string.Empty);

            if (string.IsNullOrWhiteSpace(value: name)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.NameMissing,
                    ruleName: "<unnamed>",
                    detail: "a rule declares a name"
                );
            }
            // The SAME reserved-prefix rule a state ROW name carries (see WorldStateRow.ReservedNamePrefix): '$' marks
            // what the engine mints, and nothing mints a rule — so a '$'-prefixed name is refused rather than
            // accepted, evaluated, and persisted as an authored name that reads like engine bookkeeping.
            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldStateRow.ReservedNamePrefix
            )) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.NameReserved,
                    ruleName: name,
                    detail: $"carries the reserved character '{WorldStateRow.ReservedNamePrefix}' as its first character — that prefix marks what the ENGINE mints, and nothing mints a rule"
                );
            }
            if (!seen.Add(item: name)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.NameDuplicated,
                    ruleName: name,
                    detail: "duplicates an earlier rule's name"
                );
            }

            compiled[index] = Compile(
                definition: definition,
                rule: rule!
            );
        }

        return compiled;
    }
    /// <summary>Compiles every interaction in the definition's <c>interactions</c> section, in document order — the
    /// generalized property-interaction table's one compile path. Each row desugars into a synthesized
    /// <see cref="WorldRule"/> (its co-occurrence spelled as an ordinary <see cref="ActionPredicate.CompareState"/>/
    /// <see cref="ActionPredicate.All"/> gate over the same <see cref="WorldRuleFacts.ArgMaxPrefix"/>/
    /// <see cref="WorldRuleFacts.DistancePrefix"/>/<see cref="WorldRuleFacts.RegionPrefix"/> reserved channels a
    /// hand-authored rule already reads) and rides <see cref="Compile"/> unchanged — there is no second evaluation
    /// engine, only a second authoring surface compiling to the one rule substrate. Interactions occupy their own
    /// name namespace, separate from <see cref="WorldRule.Name"/> (see <see cref="WorldInteraction"/>'s remarks).
    /// </summary>
    /// <param name="definition">The candidate document — its <c>properties</c>, <c>state</c>, and <c>placements</c>
    /// sections resolve every name an interaction can spell.</param>
    /// <returns>The compiled interactions, in authored order.</returns>
    /// <exception cref="WorldRuleException">An interaction's name is missing, reserved, or duplicated, its
    /// <c>left</c>/<c>right</c> property reference is not in the declared registry, or it otherwise fails to compile.
    /// </exception>
    public static CompiledWorldRule[] CompileAllInteractions(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var interactions = (definition.Interactions?.Interactions ?? []);

        if (interactions.Count == 0) {
            return [];
        }

        var registry = new HashSet<string>(
            collection: (definition.Properties?.Names ?? []),
            comparer: StringComparer.Ordinal
        );
        var seen = new HashSet<string>(
            capacity: interactions.Count,
            comparer: StringComparer.Ordinal
        );
        var compiled = new CompiledWorldRule[interactions.Count];

        for (var index = 0; (index < interactions.Count); index++) {
            var interaction = interactions[index];
            // WorldCellName already proved the shape at the JSON converter or console verb — a default-valued struct
            // from a programmatically built definition is the one way an empty name still reaches here, the SAME
            // caveat CompileAll's own name walk carries.
            var name = (interaction?.Name.Value ?? string.Empty);

            if (string.IsNullOrWhiteSpace(value: name)) {
                throw new WorldRuleException(
                    detail: "an interaction declares a name",
                    refusal: WorldRuleRefusal.NameMissing,
                    ruleName: "<unnamed>",
                    subject: "interaction"
                );
            }
            // The SAME reserved-prefix rule a rule name (and a state ROW name) carries: '$' marks what the engine
            // mints, and nothing mints an interaction.
            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldStateRow.ReservedNamePrefix
            )) {
                throw new WorldRuleException(
                    detail: $"carries the reserved character '{WorldStateRow.ReservedNamePrefix}' as its first character — that prefix marks what the ENGINE mints, and nothing mints an interaction",
                    refusal: WorldRuleRefusal.NameReserved,
                    ruleName: name,
                    subject: "interaction"
                );
            }
            if (!seen.Add(item: name)) {
                throw new WorldRuleException(
                    detail: "duplicates an earlier interaction's name",
                    refusal: WorldRuleRefusal.NameDuplicated,
                    ruleName: name,
                    subject: "interaction"
                );
            }

            var row = interaction!;

            // The validated-vocabulary check: 'left' is ALWAYS a property reference; 'right' is one too under
            // Distance, but names a REGION PLACEMENT under Region instead (checked structurally, not against the
            // registry — see the Region arm below).
            if (!registry.Contains(item: row.Left)) {
                throw new WorldRuleException(
                    refusal: WorldRuleRefusal.PropertyUnknown,
                    ruleName: name,
                    detail: $"'left' names '{row.Left}', which is not a registered property (see the 'properties' section)",
                    subject: "interaction"
                );
            }

            ActionPredicate gate;

            switch (row.CoOccurrence) {
                case WorldInteractionCoOccurrence.Distance:
                    if (!registry.Contains(item: row.Right)) {
                        throw new WorldRuleException(
                            refusal: WorldRuleRefusal.PropertyUnknown,
                            ruleName: name,
                            detail: $"'right' names '{row.Right}', which is not a registered property (see the 'properties' section)",
                            subject: "interaction"
                        );
                    }

                    // "The carrier most strongly tagged Left" within Range of "the carrier most strongly tagged
                    // Right" — the SAME $argmax:/$distance: spelling a hand-authored rule already uses
                    // ($distance:argmax:<row>:argmax:<row>), so it resolves through the identical operand walk
                    // (presence included: an untagged property's argmax resolves to -1, which $distance's own
                    // sentinel reads as "infinitely far", so the gate correctly stays closed with no separate
                    // presence check needed).
                    gate = new ActionPredicate.CompareState(
                        State: $"{WorldRuleFacts.DistancePrefix}argmax:{row.Left}:argmax:{row.Right}",
                        Comparison: ActionStateComparison.LessOrEqual,
                        Value: row.Range
                    );

                    break;
                case WorldInteractionCoOccurrence.Region:
                    // Presence must be checked explicitly here (unlike Distance): $region:'s occupant COUNT carries
                    // no sentinel for "nobody is tagged Left" the way $distance's infinite-distance sentinel does, so
                    // the gate spells it as a second conjunct — the SAME 'argmax != -1' presence idiom $distance
                    // gets for free.
                    gate = new ActionPredicate.All(Predicates: [
                        new ActionPredicate.CompareState(
                            State: $"{WorldRuleFacts.ArgMaxPrefix}{row.Left}",
                            Comparison: ActionStateComparison.NotEqual,
                            Value: -1f
                        ),
                        new ActionPredicate.CompareState(
                            State: $"{WorldRuleFacts.RegionPrefix}{row.Right}",
                            Comparison: ActionStateComparison.GreaterOrEqual,
                            Value: 1f
                        ),
                    ]);

                    break;
                default:
                    throw new WorldRuleException(
                        refusal: WorldRuleRefusal.PredicateKindInadmissible,
                        ruleName: name,
                        detail: $"'coOccurrence' value '{row.CoOccurrence}' is not a defined WorldInteractionCoOccurrence",
                        subject: "interaction"
                    );
            }

            var synthesized = new WorldRule(
                Name: row.Name,
                Gate: gate,
                Effects: row.Effects,
                Mode: row.Mode
            );

            compiled[index] = Compile(
                definition: definition,
                rule: synthesized
            );
        }

        return compiled;
    }

    // One resolved operand (address + value kind + read-back spelling) plus the cell kind the mixed-kind guard reads.
    private readonly record struct ResolvedOperand(CompiledWorldOperand Operand, CellKind ValueKind, string Describe);
}
