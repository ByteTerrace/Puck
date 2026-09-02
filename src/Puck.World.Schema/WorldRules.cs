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
/// <c>WorldMutation</c> of its own; see its own remarks for why that is not a door. <see cref="ActionEffect.Pose"/>
/// is the second mutation-less effect: a body pose write, not a document write.</para>
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
/// <param name="ForEach">A keyed state row to iterate, or <see langword="null"/> for one evaluation per tick. With
/// a row named, the gate and effects evaluate once per cell the row holds at the top of the tick, with
/// <c>$each</c> bound to that cell's key (a body index by convention) — the quantifier that lets one rule tick a
/// status for every body carrying it. The latch is kept per key.</param>
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
    ActionTriggerMode Mode = ActionTriggerMode.Level,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ForEach = null
);
/// <summary>The reserved <see cref="ActionPredicate.CompareState"/> channels a world rule may compare against instead
/// of a declared <see cref="WorldStateRow"/> — time, population, region occupancy, a screen-machine's live memory,
/// row aggregates/extrema, spatial facts between named bodies, a body's own reconnect-park state, and a local seat's
/// own channel value, all folded into the same string channel <c>State</c> already carries, never a new fact enum and
/// never a scheduler subsystem.
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
    /// <summary>The prefix; <c>$nearest:&lt;bodyRef&gt;:&lt;row&gt;</c> yields the index of the active body nearest to
    /// <c>bodyRef</c> (itself excluded) whose cell in the keyed <c>row</c> reads nonzero — "the closest enemy" when
    /// <c>row</c> tags enemies — or <c>-1</c> when no such body exists. Ties resolve to the lowest index. The body
    /// reference takes the same tokens <see cref="DistancePrefix"/> takes.</summary>
    public const string NearestPrefix = "$nearest:";
    /// <summary>The prefix a cell KEY may carry in place of a literal: <c>$cell:&lt;row&gt;:&lt;key&gt;</c> resolves, at
    /// every read and every firing, to the integer value of that cell spelled as a key — so an effect or operand
    /// addresses "the cell named by another cell" (the target a body's <c>target</c> cell currently names). Admitted
    /// on a <c>compareState</c> <c>key</c>/<c>comparandKey</c> and on a world-scope effect's <c>key</c>/<c>fromKey</c>;
    /// a body-reference token spells the same indirection as <c>cell:&lt;row&gt;:&lt;key&gt;</c>.</summary>
    public const string CellKeyPrefix = "$cell:";

    /// <summary>The binding vocabulary, one row per <see cref="RuleBinding"/> other than <see cref="RuleBinding.None"/>:
    /// the key token (<c>$each</c>, <c>$left</c>, <c>$right</c>), the body-reference token derived from it by
    /// <see cref="BodyTokenOf"/> (<c>each</c>, <c>left</c>, <c>right</c>), and the scope the binding is live in. Every
    /// switch and refusal that spells a binding reads this table.</summary>
    public static readonly (RuleBinding Binding, string KeyToken, string Scope)[] Bindings = [
        (RuleBinding.Each, "$each", "a rule declaring 'forEach'"),
        (RuleBinding.Left, "$left", "an interaction"),
        (RuleBinding.Right, "$right", "a Distance interaction"),
    ];

    /// <summary>The body-reference spelling of a binding's key token — the token without its leading <c>$</c>.</summary>
    /// <param name="keyToken">A <see cref="Bindings"/> key token.</param>
    public static string BodyTokenOf(string keyToken) => keyToken[1..];

    /// <summary>The prefix; <c>$channel:&lt;seat&gt;:&lt;channelName&gt;</c> reads the 1-based local seat's current value of
    /// a declared <c>channels[]</c> row as its body integrates it that tick — the drained
    /// <see cref="Puck.Commands.CommandSnapshot"/> read folded with co-driving contributions and the admitted held
    /// overlay (the path a held sample such as a probe axis reaches a channel by), the same value <c>body.channels</c>
    /// reports as <c>composed</c>. The value rides the channel's own native <see cref="Puck.Maths.FixedQ4816"/>
    /// domain unchanged — a bipolar/unipolar channel's <c>1</c> is already "fully pressed/1.0", so an authored
    /// <c>compareState($channel:1:portal, greaterOrEqual, 1)</c> needs no rescale, unlike <see cref="ParkedPrefix"/>/
    /// <see cref="MachinePrefix"/>'s raw integer counts. Deterministic by construction: every contributor is the
    /// tick's own replay input. <c>seat</c> is bounds-checked at compile time against <c>population.localSeats</c>;
    /// <c>channelName</c> against the declared <c>channels[]</c> rows. An unseated/absent seat reads <c>0</c> — the
    /// convention <see cref="ParkedPrefix"/>/<see cref="MachinePrefix"/>/<see cref="RegionPrefix"/> already set.</summary>
    public const string ChannelPrefix = "$channel:";
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
    /// <summary>The prefix; <c>$link:&lt;adjacencyName&gt;</c> reads how many simulation ticks have passed since the
    /// named <c>adjacencies</c> row last received a delivered neighbour refresh — <c>0</c> the tick a refresh landed,
    /// rising by one per tick while nothing arrives. The neutral-falsy convention
    /// <see cref="MachinePrefix"/>/<see cref="RegionPrefix"/>/<see cref="ParkedPrefix"/> already set: an edge whose
    /// <see cref="WorldAdjacency.LivenessGraceSeconds"/> is unauthored (liveness sensing disabled) reads <c>0</c>
    /// forever, so a staleness gate (<c>compareState($link:north, greaterOrEqual, 240)</c>) stays closed rather than
    /// spuriously opening.
    /// <para>The argument is an <c>adjacencies</c> row name, refused at compile time
    /// (<see cref="WorldRuleRefusal.LinkChannelMalformed"/>) when no such row is declared. Unrelated to the machine
    /// cable groups (<c>screen.link</c>, <see cref="WorldMachineCable"/>); this channel names a federation
    /// seam.</para>
    /// <para>Both this value and the <c>linkEstablished</c>/<c>linkDropped</c> event family derive from the taped
    /// per-tick delivery-refresh observations, so a replay reproduces a rule gated on it — see
    /// <c>Server.WorldEventFeed</c>'s own remarks for the exact taped boundary.</para></summary>
    public const string LinkPrefix = "$link:";
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
    /// <summary>The prefix; <c>$symmetry:&lt;function&gt;[:&lt;argument&gt;]:&lt;row&gt;</c> reads a cell holding a
    /// symmetry-lattice node (0..239, <c>Puck.Maths.SymmetryLattice</c>) through one of the lattice's own maps — the
    /// row is named last and the operand's <c>key</c> addresses the cell exactly as an ordinary row read does, so a
    /// per-body node table reads through <c>$each</c> or a <c>$cell:</c> indirection unchanged. Functions:
    /// <c>ring</c> (the node's ring, 0..7), <c>antipode</c>, <c>canonicalRay</c> (the smaller node of the antipodal
    /// pair), <c>cycle:&lt;steps&gt;</c> (the node carried that many positions around its ring, negative walks back),
    /// <c>reflect:&lt;other&gt;</c> (the node reflected through the other node's hyperplane), <c>orthogonal:&lt;other&gt;</c>
    /// (1 when the two nodes' rays are orthogonal, else 0), and <c>projectionX</c>/<c>projectionY</c> (the node's
    /// point on the plane of eight rings, a fixed value). <c>&lt;other&gt;</c> is a node literal or
    /// <c>cell:&lt;row&gt;[.&lt;key&gt;]</c>, a second cell read live. A source cell holding no node (outside 0..239)
    /// reads <c>-1</c> for the node-valued functions, <c>0</c> for <c>orthogonal</c> and the projections — the
    /// ordinary "absent reads as the neutral value" convention. A <c>fixed</c> source cell's node is the whole part of
    /// its value, the same reading a <see cref="WorldStateCycle"/> lattice output takes. With
    /// <see cref="WorldStateCycle"/>'s <c>Node</c> output driving a row and this channel reading it, a rule can gate
    /// on the ring a walk has reached, reflect one player's arrangement onto another's, or test two placements for
    /// orthogonality — the lattice's whole symmetry group, reached through <c>compareState</c>/<c>fromState</c>.</summary>
    public const string SymmetryPrefix = "$symmetry:";
    /// <summary>Compares the server's own completed-tick counter — <c>compareState("$tick", greaterOrEqual, 600)</c>
    /// is "at 2.5 seconds", with no clock read anywhere.</summary>
    public const string Tick = "$tick";
}
/// <summary>Which symmetry-lattice map a <see cref="WorldRuleFacts.SymmetryPrefix"/> operand applies to its source
/// node.</summary>
public enum WorldSymmetryFunction : byte {
    /// <summary>The node's ring, 0..7.</summary>
    Ring,
    /// <summary>The antipodal node.</summary>
    Antipode,
    /// <summary>The smaller node of the antipodal pair — a stable unoriented-ray key.</summary>
    CanonicalRay,
    /// <summary>The node carried <c>argument</c> positions around its ring.</summary>
    Cycle,
    /// <summary>The node reflected through the other node's hyperplane.</summary>
    Reflect,
    /// <summary>1 when the node's ray and the other node's ray are orthogonal, else 0.</summary>
    Orthogonal,
    /// <summary>The node's projected X coordinate, a fixed value.</summary>
    ProjectionX,
    /// <summary>The node's projected Y coordinate, a fixed value.</summary>
    ProjectionY,
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

    /// <summary>The body whose index a state cell (<c>Row</c>, <c>Key</c>) holds — <c>cell:&lt;row&gt;:&lt;key&gt;</c>.</summary>
    Cell,

    /// <summary>The body a <see cref="RuleBinding"/> names this evaluation — <c>each</c>/<c>left</c>/<c>right</c>;
    /// <c>Index</c> carries the binding.</summary>
    Binding,
}
/// <summary>One resolved body reference — see <see cref="CompiledBodyRefKind"/>.</summary>
/// <param name="Kind">How the body is named.</param>
/// <param name="Index">The literal 0-based index for <see cref="CompiledBodyRefKind.Literal"/>; unused otherwise.</param>
/// <param name="Row">The keyed row name for <see cref="CompiledBodyRefKind.ArgMax"/>/<see cref="CompiledBodyRefKind.ArgMin"/>
/// and the indirection row for <see cref="CompiledBodyRefKind.Cell"/>; <see langword="null"/> otherwise.</param>
/// <param name="Key">The indirection cell's key for <see cref="CompiledBodyRefKind.Cell"/>; <see langword="null"/> otherwise.</param>
public readonly record struct CompiledBodyRef(CompiledBodyRefKind Kind, int Index, string? Row, string? Key = null);
/// <summary>A state cell address whose integer value is read as a cell KEY at evaluation time
/// (<see cref="WorldRuleFacts.CellKeyPrefix"/>).</summary>
/// <param name="Row">The row holding the indirection cell.</param>
/// <param name="Key">The indirection cell's key.</param>
/// <param name="Binding">The bound body read as the key instead, when not <see cref="RuleBinding.None"/>; then
/// <paramref name="Row"/>/<paramref name="Key"/> are empty.</param>
public readonly record struct CompiledCellRef(string Row, string Key, RuleBinding Binding = RuleBinding.None);
/// <summary>A name bound during one evaluation of a rule or interaction — the body index a key token
/// <c>$each</c>/<c>$left</c>/<c>$right</c> or a body-reference token <c>each</c>/<c>left</c>/<c>right</c> reads.</summary>
public enum RuleBinding : byte {
    /// <summary>No binding — the literal key or body index applies.</summary>
    None,

    /// <summary>The iterated cell key of a <see cref="WorldRule.ForEach"/> rule.</summary>
    Each,

    /// <summary>The carrier matched as an interaction's <see cref="WorldInteraction.Left"/>.</summary>
    Left,

    /// <summary>The carrier matched as an interaction's <see cref="WorldInteraction.Right"/> (Distance only).</summary>
    Right,
}
/// <summary>The co-occurrence an interaction evaluates over every carrier pair — compiled from
/// <see cref="WorldInteraction"/>; the evaluator binds <see cref="RuleBinding.Left"/>/<see cref="RuleBinding.Right"/>
/// per match.</summary>
/// <param name="Left">The keyed tag row whose nonzero cells are the left carriers.</param>
/// <param name="Right">The keyed tag row of right carriers (Distance), or the placement id (Region).</param>
/// <param name="CoOccurrence">How a pair is detected.</param>
/// <param name="Range">The Distance threshold.</param>
public readonly record struct CompiledInteraction(string Left, string Right, WorldInteractionCoOccurrence CoOccurrence, FixedQ4816 Range);
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

    /// <summary>Simulation ticks since one named adjacency row last received a delivered neighbour refresh
    /// (<see cref="WorldRuleFacts.LinkPrefix"/>).</summary>
    LinkStaleness,

    /// <summary>One local seat's own folded channel value (<see cref="WorldRuleFacts.ChannelPrefix"/>).</summary>
    Channel,

    /// <summary>The nearest tagged body's index (<see cref="WorldRuleFacts.NearestPrefix"/>).</summary>
    Nearest,

    /// <summary>A <see cref="WorldRuleFacts.SymmetryPrefix"/> read: a cell's node through one symmetry-lattice map.</summary>
    Symmetry,
}
/// <summary>One resolved operand of a world-rule comparison — the (<see cref="Kind"/>, <see cref="Row"/>,
/// <see cref="Key"/>) address plus the <see cref="Screen"/>/<see cref="Address"/> machine coordinates, the live
/// quantity <c>WorldServer.RuleGateOpen</c> reads to a <see cref="FixedQ4816"/>. Both sides of a
/// <see cref="ActionPredicate.CompareState"/> conjunct — the primary and, when spelled, the comparand — are the same
/// operand type, read by the same <c>ReadWorldFact</c> helper, so the two sides can never drift into two readings of
/// one name.</summary>
/// <param name="Kind">Which live quantity this operand reads.</param>
/// <param name="Row">The state row name for <see cref="WorldRuleFactKind.StateCell"/>, the placement id for
/// <see cref="WorldRuleFactKind.RegionOccupancy"/>, the adjacency row name for
/// <see cref="WorldRuleFactKind.LinkStaleness"/>, or <see langword="null"/> otherwise.</param>
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
/// <param name="Seat">The 0-based local-seat body index for <see cref="WorldRuleFactKind.Channel"/>; unused
/// otherwise.</param>
/// <param name="ChannelOrdinal">The declared channel's document-order ordinal (the same ordinal
/// <c>WorldChannelTable.Compile</c> assigns) for <see cref="WorldRuleFactKind.Channel"/>; unused otherwise.</param>
/// <param name="KeyFrom">For <see cref="WorldRuleFactKind.StateCell"/>, the cell key read by indirection
/// (<see cref="WorldRuleFacts.CellKeyPrefix"/>) in place of <paramref name="Key"/>; <see langword="null"/> otherwise.</param>
/// <param name="Symmetry">The lattice map a <see cref="WorldRuleFactKind.Symmetry"/> operand applies to its source node.</param>
/// <param name="SymmetryArgument">The literal argument of that map — the step count of <see cref="WorldSymmetryFunction.Cycle"/>,
/// or the other node of <see cref="WorldSymmetryFunction.Reflect"/>/<see cref="WorldSymmetryFunction.Orthogonal"/>
/// when <paramref name="SymmetryOtherCell"/> is <see langword="null"/>.</param>
/// <param name="SymmetryOtherCell">The cell the other node is read from live, or <see langword="null"/> for the literal
/// <paramref name="SymmetryArgument"/>; an empty key addresses a slot row's own cell.</param>
/// <param name="ValueKind">The raw encoding returned by this operand. Carrying it beside the address lets the
/// runtime compare integer cells without narrowing them through binary32.</param>
/// <param name="StateHandle">The compiled world-lane row handle for state-backed operands; invalid for channels
/// that do not read a state row.</param>
public readonly record struct CompiledWorldOperand(
    WorldRuleFactKind Kind,
    string? Row,
    string? Key,
    int Screen = 0,
    int Address = 0,
    WorldStateReduceOp Reduce = WorldStateReduceOp.None,
    CompiledBodyRef? BodyA = null,
    CompiledBodyRef? BodyB = null,
    int Seat = 0,
    int ChannelOrdinal = 0,
    CompiledCellRef? KeyFrom = null,
    WorldSymmetryFunction Symmetry = WorldSymmetryFunction.Ring,
    long SymmetryArgument = 0L,
    CompiledCellRef? SymmetryOtherCell = null,
    CellKind ValueKind = CellKind.Fixed,
    WorldStateHandle StateHandle = default
);
/// <summary>One compiled, flattened conjunct of a world rule's gate — <see cref="ActionPredicate.All"/> flattens away
/// at compile time exactly as a per-body gate does, so evaluation walks one flat array with no recursion.</summary>
/// <param name="Left">The primary operand — the <c>(State, Key)</c> side of the authored <c>compareState</c>.</param>
/// <param name="Comparison">The comparison to apply.</param>
/// <param name="Value">The authored constant comparand, converted directly from its exact decimal token to the
/// left operand's raw cell encoding at compile time — read only when
/// <paramref name="Comparand"/> is <see langword="null"/> (the constant spelling).</param>
/// <param name="ValueKind">The encoding carried by <paramref name="Value"/>.</param>
/// <param name="Comparand">The comparand operand — another row/reserved channel read live on the same terms as
/// <paramref name="Left"/> (the <c>(ComparandState, ComparandKey)</c> spelling) — or <see langword="null"/> when the
/// comparand is the authored constant <paramref name="Value"/> instead.</param>
/// <param name="Describe">The authored spelling of this conjunct, for the <c>world.rules</c> read-back — an
/// <see cref="ActionPredicate.All"/> gate prints its predicates rather than a type name, which is the whole point of
/// keeping the text beside the compiled form.</param>
public readonly record struct CompiledWorldPredicate(
    CompiledWorldOperand Left,
    ActionStateComparison Comparison,
    long Value,
    CellKind ValueKind,
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

    /// <summary>Write a session snapshot of the world to its own file (<see cref="ActionEffect.Save"/>) — submits no
    /// mutation; see <see cref="ActionEffect.Save"/>'s own remarks for why.</summary>
    Save,

    /// <summary>Teleport a body to a pose (<see cref="ActionEffect.Pose"/>) — submits no mutation; a pose is body
    /// state, not document state.</summary>
    Pose,
}
/// <summary>One compiled world-rule effect. Every kind but <see cref="WorldRuleEffectKind.Save"/> submits an ordinary
/// mutation (<c>WorldMutation.UpsertStateCell</c>, <c>WorldMutation.Generate</c>,
/// <c>WorldMutation.UpsertHudPanel</c>/<c>WorldMutation.RemoveHudPanel</c>, or
/// <c>WorldMutation.UpsertPlacement</c>/<c>WorldMutation.RemovePlacement</c>) under
/// <see cref="WorldPrincipal.World"/>, so journal and undo cover them exactly like any other write; <c>Save</c> rides
/// <c>WorldServer.FireWorldRuleEffect</c>'s own I/O tap directly instead (see <see cref="ActionEffect.Save"/>).</summary>
/// <param name="Kind">Which mutation this effect submits.</param>
/// <param name="Row">The destination state row name for <see cref="WorldRuleEffectKind.Write"/>/
/// <see cref="WorldRuleEffectKind.Generate"/>, the panel/placement id for
/// <see cref="WorldRuleEffectKind.RemoveHudPanel"/>/<see cref="WorldRuleEffectKind.RemovePlacement"/>, or the
/// spawn-point id (empty when <paramref name="Pose"/> carries a literal) for <see cref="WorldRuleEffectKind.Pose"/>.</param>
/// <param name="Key">The destination cell key; the body index for <see cref="WorldRuleEffectKind.Pose"/>.</param>
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
/// <param name="KeyFrom">The destination cell's key indirection (<see cref="WorldRuleFacts.CellKeyPrefix"/>), or
/// <see langword="null"/> when <paramref name="Key"/> is literal.</param>
/// <param name="Pose">The literal pose, for a <see cref="WorldRuleEffectKind.Pose"/> effect authored with a
/// position; <see langword="null"/> when <paramref name="Row"/> names a spawn point instead.</param>
/// <param name="Text">The text literal a <see cref="WorldRuleEffectKind.Write"/> into a <c>kind=text</c> row
/// carries; <see langword="null"/> for a numeric write.</param>
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
    CompiledWorldOperand? From = null,
    CompiledWorldPose? Pose = null,
    string? Text = null,
    CompiledCellRef? KeyFrom = null
);
/// <summary>A literal body pose compiled to deterministic numerics — angles in radians.</summary>
/// <param name="Position">The world position.</param>
/// <param name="YawRadians">The yaw about +Y.</param>
/// <param name="PitchRadians">The pitch about the body right.</param>
/// <param name="RollRadians">The roll about the body forward.</param>
public readonly record struct CompiledWorldPose(FixedVector3 Position, FixedQ4816 YawRadians, FixedQ4816 PitchRadians, FixedQ4816 RollRadians);
/// <summary>One compiled rule: its name, its mode, the flattened gate, and the compiled effects.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="Mode">Level or edge (see <see cref="ActionTriggerMode"/>).</param>
/// <param name="Gate">The flattened conjunction; empty means "always".</param>
/// <param name="Effects">The compiled effects, in authored order.</param>
/// <param name="ForEach">The keyed row a rule iterates (<see cref="WorldRule.ForEach"/>), or <see langword="null"/>.</param>
/// <param name="Interaction">The co-occurrence an interaction evaluates, or <see langword="null"/> for a rule.</param>
public sealed record CompiledWorldRule(string Name, ActionTriggerMode Mode, CompiledWorldPredicate[] Gate, CompiledWorldEffect[] Effects, string? ForEach = null, CompiledInteraction? Interaction = null);
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

    /// <summary>A <c>$symmetry:</c> channel does not spell <c>$symmetry:&lt;function&gt;[:&lt;argument&gt;]:&lt;row&gt;</c>
    /// — an unknown function, a function given an argument it does not take (or missing one it needs), an argument
    /// that is neither a node literal nor <c>cell:&lt;row&gt;[.&lt;key&gt;]</c>, or a source row that is not a declared
    /// numeric row.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$symmetry:' channel does not spell '$symmetry:<function>[:<argument>]:<row>' with a known function, a well-formed argument and a declared numeric source row", kind: RefusalKind.Verdict)]
    SymmetryChannelMalformed,

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

    /// <summary>A <c>$link:</c> channel does not spell exactly one adjacency row name, or names a row the
    /// <c>adjacencies</c> section does not declare.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$link:' channel does not name exactly one declared 'adjacencies' row", kind: RefusalKind.Verdict)]
    LinkChannelMalformed,

    /// <summary>A <c>$channel:</c> channel does not spell <c>$channel:&lt;seat&gt;:&lt;channelName&gt;</c> with
    /// <c>seat</c> in <c>1..population.localSeats</c> and <c>channelName</c> naming a declared <c>channels[]</c>
    /// row.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$channel:' channel does not spell '$channel:<seat>:<channelName>' with seat in 1..population.localSeats and channelName naming a declared 'channels[]' row", kind: RefusalKind.Verdict)]
    ChannelMalformed,

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

    /// <summary>A <c>pose</c> effect names a <c>spawnPoint</c> the document's <c>spawnPoints</c> section does not
    /// declare (the implicit <c>origin</c> counts as declared when the section is absent).</summary>
    [Refusal(door: "world.rule.compile", condition: "a 'pose' effect names a 'spawnPoint' the document's 'spawnPoints' section does not declare", kind: RefusalKind.Verdict)]
    SpawnPointUnknown,

    /// <summary>A <c>pose</c> effect authors both or neither of <c>spawnPoint</c> and <c>position</c>.</summary>
    [Refusal(door: "world.rule.compile", condition: "a 'pose' effect authors both or neither of 'spawnPoint' and 'position'", kind: RefusalKind.Verdict)]
    PoseAmbiguous,
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
