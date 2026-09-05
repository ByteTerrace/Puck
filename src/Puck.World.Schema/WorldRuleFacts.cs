namespace Puck.World;

/// <summary>The reserved channels a world rule reads that only a world can answer — bodies, distance, line of sight,
/// navigation, screens, links, regions, the population — spelled beside the state-neutral channels of
/// <see cref="RuleFacts"/> and resolved by the same compiler. Every one carries the reserved name prefix no authored
/// row may use.</summary>
public static class WorldRuleFacts {
    /// <summary>The prefix; <c>$argmax:&lt;row&gt;</c> reads a keyed row's cells and yields the winning cell's key —
    /// not the winning value — as a body index. The genuinely new primitive: a rule can name a body. A row driving
    /// this channel is a convention, enforced at compile time (<c>WorldRuleRefusal.ArgRowNotKeyed</c>) and at
    /// read time (a cell whose key does not parse as a non-negative index the population actually holds is simply
    /// excluded from consideration, never a hard refusal — the same "an ineligible candidate reads as absent, not an
    /// error" posture <see cref="MachinePrefix"/>'s unbooted-machine case already sets): author a keyed row whose
    /// cell keys are body indices spelled as plain integers (<c>"0"</c>, <c>"3"</c>, …) — e.g. a per-body
    /// <c>threat</c> tally — and <c>$argmax:threat</c> is "the body with the highest tally". Appending
    /// <c>:where:&lt;filterRow&gt;</c> admits only body indices whose numeric filter cell is nonzero. Ties resolve to the
    /// lowest eligible index, deterministically. An empty or entirely-ineligible row yields <c>-1</c> ("no body"),
    /// which composes with <see cref="DistancePrefix"/>/<see cref="LineOfSightPrefix"/>'s <c>argmax:&lt;row&gt;</c>
    /// body-reference token exactly as a literal <c>body:&lt;n&gt;</c> does — a spatial fact against "no body" simply
    /// never satisfies (see <c>WorldServer.ReadBodyDistance</c>'s sentinel).</summary>
    public const string ArgMaxPrefix = "$argmax:";
    /// <summary>The prefix; <c>$argmin:&lt;row&gt;</c> is <see cref="ArgMaxPrefix"/>'s dual — the body naming the
    /// smallest cell; it accepts the same optional <c>:where:&lt;filterRow&gt;</c> suffix.</summary>
    public const string ArgMinPrefix = "$argmin:";
    /// <summary>The prefix; <c>$nearest:&lt;bodyRef&gt;:&lt;row&gt;</c> yields the index of the active body nearest to
    /// <c>bodyRef</c> (itself excluded) whose cell in the keyed <c>row</c> reads nonzero — "the closest enemy" when
    /// <c>row</c> tags enemies — or <c>-1</c> when no such body exists. Ties resolve to the lowest index. The body
    /// reference takes the same tokens <see cref="DistancePrefix"/> takes.</summary>
    public const string NearestPrefix = "$nearest:";
    /// <summary>The prefix; <c>$clock:&lt;music&gt;:phaseError</c> reads the signed tick distance from the world's
    /// musical clock's current position to the nearest beat — positive after the beat, negative ahead of the next
    /// one, magnitude at most half a beat. A hit window is an ordinary <c>compareState</c> range over it (no
    /// dedicated effect or asset family): the same read at every firing tick a kit action's own edge trigger stamps
    /// a state cell for. <c>music</c> must name the document's declared music row (the only clock a world may
    /// author); a world with none has no clock to read and the operand refuses at compile time.</summary>
    public const string ClockPrefix = "$clock:";
    /// <summary>The prefix a cell KEY may carry to address a row by a genuine (observer, subject) pair rather than
    /// one body: <c>$pair:&lt;bodyRefA&gt;:&lt;bodyRefB&gt;</c> resolves, at every read and every firing, to the
    /// composite key <c>"&lt;a&gt;_&lt;b&gt;"</c> (underscore, since <see cref="CellName"/> reserves <c>:</c>)
    /// the two live body references (the same grammar <see cref="DistancePrefix"/>/<see cref="LineOfSightPrefix"/>
    /// spend both halves on — a literal <c>body:&lt;n&gt;</c>, an <c>argmax:&lt;row&gt;</c>/<c>argmin:&lt;row&gt;</c>
    /// reduction, a <c>cell:&lt;row&gt;:&lt;key&gt;</c> indirection, or a bound <c>each</c>/<c>left</c>/<c>right</c>)
    /// resolve to — so a keyed row can hold one cell PER PAIR (an observer's own impression of one particular
    /// subject) instead of one cell per body. <c>(a, b)</c> and <c>(b, a)</c> name different cells: the pair is
    /// directed, exactly as "how much A trusts B" and "how much B trusts A" are two different numbers. Admitted
    /// everywhere <see cref="RuleFacts.CellKeyPrefix"/> is — a <c>compareState</c> <c>key</c>/<c>comparandKey</c>, a
    /// world-scope effect's <c>key</c>/<c>fromKey</c>, a flock affinity's own key — because both resolve through the
    /// same <c>CompiledCellRef</c> indirection carrier.</summary>
    public const string PairKeyPrefix = "$pair:";

    /// <summary>The prefix; <c>$channel:&lt;seat&gt;:&lt;channelName&gt;</c> reads the 1-based local seat's current value of
    /// a declared <c>channels[]</c> row as its body integrates it that tick — the drained
    /// <c>CommandSnapshot</c> read folded with co-driving contributions and the admitted held
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
    /// <summary>The prefix; <c>$upright:&lt;bodyRef&gt;</c> reads one named body's own up axis (its local +Y rotated
    /// by its live orientation) dotted against the world up its gravity opposes — <c>1</c> standing exactly upright,
    /// <c>0</c> lying exactly on its side, negative past horizontal — the same single-body-reference grammar
    /// <see cref="ParkedPrefix"/> spends. A gate compares it directly against an authored cosine threshold
    /// (<c>compareState($upright:body:12, greaterOrEqual, 0.866)</c> admits up to 30 degrees of tilt) rather than a
    /// second reserved channel duplicating the angle — the tabletop primitive's own worked use is gating a piece's
    /// board occupancy derive on this so a knocked-over piece reads as displaced rather than occupying its last
    /// resolved cell. A body reference resolving to no live body reads <c>1</c> (perfectly upright) — the neutral
    /// value for an absent body, since nothing about "no body" should ever read as knocked over.</summary>
    public const string UprightPrefix = "$upright:";
    /// <summary>The prefix; <c>$nav:&lt;bodyRef&gt;:&lt;facet&gt;</c> reads one body's live route state. Facets are
    /// <c>hasPath</c>, <c>active</c>, <c>arrived</c>, <c>unreachable</c>, <c>pending</c>, <c>capacity</c>, and <c>remaining</c> waypoints.</summary>
    public const string NavigationPrefix = "$nav:";
    /// <summary>The prefix; <c>$link:&lt;adjacencyName&gt;</c> reads how many simulation ticks have passed since the
    /// named <c>adjacencies</c> row last received a delivered neighbour refresh — <c>0</c> the tick a refresh landed,
    /// rising by one per tick while nothing arrives. The neutral-falsy convention
    /// <see cref="MachinePrefix"/>/<see cref="RegionPrefix"/>/<see cref="ParkedPrefix"/> already set: an edge whose
    /// <c>WorldAdjacency.LivenessGraceSeconds</c> is unauthored (liveness sensing disabled) reads <c>0</c>
    /// forever, so a staleness gate (<c>compareState($link:north, greaterOrEqual, 240)</c>) stays closed rather than
    /// spuriously opening.
    /// <para>The argument is an <c>adjacencies</c> row name, refused at compile time
    /// (<c>WorldRuleRefusal.LinkChannelMalformed</c>) when no such row is declared. Unrelated to the machine
    /// cable groups (<c>screen.link</c>, <c>WorldMachineCable</c>); this channel names a federation
    /// seam.</para>
    /// <para>Both this value and the <c>linkEstablished</c>/<c>linkDropped</c> event family derive from the taped
    /// per-tick delivery-refresh observations, so a replay reproduces a rule gated on it — see
    /// <c>Server.WorldEventFeed</c>'s own remarks for the exact taped boundary.</para></summary>
    public const string LinkPrefix = "$link:";
    /// <summary>The prefix; <c>$machine:&lt;screen&gt;:&lt;address&gt;</c> compares one byte (0..255) read live off a
    /// declared <c>WorldScreen</c>'s booted machine — the same <c>IWorldMachineMemoryPeek.TryPeek</c> primitive
    /// <c>WorldAddonMemoryWatch</c> already rides, called directly instead of accumulated as a change event. A
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
    /// <summary>Reads <c>1</c> when every active rigid body (a kit authoring <c>rigid</c>) currently latches
    /// <see cref="Puck.Physics.Motion.ActionFact.Resting"/>, <c>0</c> otherwise — vacuously <c>1</c> when the world
    /// authors no rigid body, so a turn-waiting rule composes without a special case for a world that never wired one
    /// in.</summary>
    public const string PhysicsQuiescent = "$physics:quiescent";
    /// <summary>The prefix; <c>$region:&lt;placementId&gt;</c> compares that placement's live region occupant count —
    /// the same count the world-events feed already tracks per tick, read rather than duplicated.</summary>
    /// <remarks><b>Does not collapse into <see cref="DistancePrefix"/>.</b> A <c>WorldPlacementRegion</c> is
    /// geometrically a sphere (<c>Radius</c> from the placement's own position), so "is body N inside the region"
    /// alone would in fact reduce to a distance test the distance primitive can express. The count this channel reads
    /// does not: it is an aggregate over the whole active population (however many of up to 4096 bodies currently sit
    /// inside), while <see cref="DistancePrefix"/>/<see cref="LineOfSightPrefix"/> only ever name two fixed bodies —
    /// there is no "for every active body" quantifier in the rule vocabulary, and this channel's O(1) read is a cached
    /// counter <c>Server.WorldEventFeed</c> already maintains incrementally as bodies cross the boundary, never
    /// recomputed. Replacing it with the distance primitive would mean scanning up to 4096 bodies' distances per rule
    /// per tick to recover a number the engine already tracks for free — a real regression for the one consumer that
    /// exists today. Kept as its own case, deliberately.</remarks>
    public const string RegionPrefix = "$region:";
}
