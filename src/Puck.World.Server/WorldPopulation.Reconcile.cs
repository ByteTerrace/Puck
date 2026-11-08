using Puck.World.Protocol;

namespace Puck.World.Server;

// The one implementation of what an install does to the slot table: the structural reconcile of the inhabited
// placements, the cell-driven counts and the census re-clamp. Every occupancy decision a reconcile makes (which slot a
// body joins, which inhabitant a shrink retires, which census bodies the re-clamp keeps) is written once, generic over
// ISlots. The live table (LiveSlots) applies each change to the entries, minting or dropping bodies and reporting peer
// events; a forecast (ForecastSlots) records it on a scratch copy and changes nothing else. ReconcileKeeps runs the
// forecast, so a retire rule added to the reconcile is one the forecast answers from, with no second copy to keep in
// step.
public sealed partial class WorldPopulation {
    // Sentinel meaning "no cell-driven inhabit count has ever been read for this placement ordinal" — distinct from
    // every representable raw cell value, including 0 (an authored count cell legitimately starts empty).
    private const long NoInhabitCountObserved = long.MinValue;

    // Per-placement-ordinal cache of the last raw cell value the cell-driven reconcile resolved for a cell-driven
    // inhabit facet — a placement with no such facet, or a literal count, is never written here. Resized (and its
    // fresh slots re-seeded to NoInhabitCountObserved) only when the placement count itself changes, which already
    // allocates elsewhere in the same structural install; a quiet tick that touches no relevant cell reads this
    // array and writes nothing.
    private long[] m_inhabitCountCache = [];
    // The forecast's scratch slot table, reused from one forecast to the next.
    private SlotForecast? m_forecast;

    // What a reconcile reads of the slot table and the changes it makes to it.
    private interface ISlots {
        bool IsActive(int index);
        string? PlacementOf(int index);
        bool IsRemoteHuman(int index);
        bool IsTransferred(int index);
        int SimulatedCount { get; set; }
        // The cached raw count for a placement ordinal, or NoInhabitCountObserved.
        long CachedCount(int ordinal);
        void CacheCount(int ordinal, long raw);
        // Sizes the count cache to the placement count, re-seeding it when the count changed.
        void SizeCountCache(int placements);
        void InvalidateCountCache();
        void AdmitInhabitant(WorldDefinition definition, int index, WorldPlacement placement, WorldPlacementInhabit inhabit, byte kitIndex, int ordinal, int desired);
        void RetireInhabitant(int index);
        void BindInhabitant(int index, WorldPlacement placement, byte kitIndex);
        // `reports` is whether the change is a peer lifecycle event (SelectCensus' own remarks).
        void AdmitCensus(int index, bool reports);
        void RetireCensus(int index, bool reports);
        void TableFull(WorldPlacement placement);
        void CountClamped(WorldPlacement placement, string row, int desired, int bound);
        // The occupancy moved: the live table bumps its revision.
        void Revise();
        // A pass that moved inhabitants finished: the live table rebuilds its placement ordinal table and revises.
        void Settle(WorldDefinition definition);
    }

    // The live table: each change is the population's own.
    private readonly struct LiveSlots(WorldPopulation population, List<WorldPeerEventEntry>? admitted, List<WorldPeerEventEntry>? disconnected) : ISlots {
        public bool IsActive(int index) => population.m_entries[index].Active;
        public string? PlacementOf(int index) => population.m_entries[index].PlacementId;
        public bool IsRemoteHuman(int index) => population.m_entries[index].IsRemoteHuman;
        public bool IsTransferred(int index) => population.m_entries[index].IsAuthorityTransferred;
        public int SimulatedCount {
            get => population.m_simulatedCount;
            set => population.m_simulatedCount = value;
        }
        public long CachedCount(int ordinal) => population.m_inhabitCountCache[ordinal];
        public void CacheCount(int ordinal, long raw) => population.m_inhabitCountCache[ordinal] = raw;
        public void SizeCountCache(int placements) {
            if (population.m_inhabitCountCache.Length != placements) {
                var cache = new long[placements];

                Array.Fill(
                    array: cache,
                    value: NoInhabitCountObserved
                );
                population.m_inhabitCountCache = cache;
            }
        }
        public void InvalidateCountCache() => Array.Fill(
            array: population.m_inhabitCountCache,
            value: NoInhabitCountObserved
        );
        public void AdmitInhabitant(WorldDefinition definition, int index, WorldPlacement placement, WorldPlacementInhabit inhabit, byte kitIndex, int ordinal, int desired) {
            population.ActivateInhabitant(
                definition: definition,
                desiredCount: desired,
                index: index,
                inhabit: inhabit,
                kitIndex: kitIndex,
                ordinal: ordinal,
                placement: placement
            );
            admitted?.Add(item: population.PeerEventEntry(index: index));
        }
        public void RetireInhabitant(int index) {
            disconnected?.Add(item: population.PeerEventEntry(index: index));
            population.RetireInhabitant(index: index);
        }
        public void BindInhabitant(int index, WorldPlacement placement, byte kitIndex) => population.BindInhabitant(
            entry: population.m_entries[index],
            kitIndex: kitIndex,
            placement: placement
        );
        public void AdmitCensus(int index, bool reports) {
            population.ActivateSimulated(index: index);
            population.m_entries[index].Active = true;

            if (reports) {
                admitted?.Add(item: population.PeerEventEntry(index: index));
            }
        }
        public void RetireCensus(int index, bool reports) {
            var entry = population.m_entries[index];

            if (reports) {
                disconnected?.Add(item: population.PeerEventEntry(index: index));
            }
            // A re-activation mints a fresh body at the canonical spawn.
            entry.Body = null;
            entry.Active = false;
        }
        public void TableFull(WorldPlacement placement) {
            if (population.NarrationHub is { HasNarrationSink: true } hub) {
                hub.Narrate(
                    channel: "world.placement",
                    text: $"[world.placement: inhabited '{placement.Id}' has no free entity slot — the {population.Capacity}-slot table is full]"
                );
            }
        }
        public void CountClamped(WorldPlacement placement, string row, int desired, int bound) {
            if (population.NarrationHub is { HasNarrationSink: true } hub) {
                hub.Narrate(
                    channel: "world.placement",
                    text: $"[world.placement: inhabited '{placement.Id}' count {desired} of {row} (clamped by {bound})]"
                );
            }
        }
        public void Revise() => population.m_revision++;
        public void Settle(WorldDefinition definition) {
            population.RebuildPlacementOrdinalTable(definition: definition);
            population.m_revision++;
        }
    }

    // A scratch copy of the slot facts a reconcile reads, taken from the live table on the forecast's first change, and
    // which slots the forecast retired or admitted (a slot retired and admitted again holds a different body).
    private sealed class SlotForecast {
        public bool[] Active = [];
        public string?[] Placement = [];
        public bool[] Replaced = [];
        public bool Copied;
        public bool FreshCache;
        public int CacheLength;
        public int SimulatedCount;

        public void Begin(WorldPopulation population) {
            Copied = false;
            FreshCache = false;
            CacheLength = population.m_inhabitCountCache.Length;
            SimulatedCount = population.m_simulatedCount;
        }
        public void Copy(WorldPopulation population) {
            if (Copied) {
                return;
            }

            var capacity = population.Capacity;

            if (Active.Length != capacity) {
                Active = new bool[capacity];
                Placement = new string?[capacity];
                Replaced = new bool[capacity];
            }

            for (var index = 0; (index < capacity); index++) {
                Active[index] = population.m_entries[index].Active;
                Placement[index] = population.m_entries[index].PlacementId;
                Replaced[index] = false;
            }

            Copied = true;
        }
    }

    // The forecast: reads the live table until its first change, then its scratch copy. It writes nothing outside the
    // forecast: no body, no event, no narration, no cache entry and no revision.
    private readonly struct ForecastSlots(WorldPopulation population, SlotForecast forecast) : ISlots {
        public bool IsActive(int index) => (forecast.Copied
            ? forecast.Active[index]
            : population.m_entries[index].Active);
        public string? PlacementOf(int index) => (forecast.Copied
            ? forecast.Placement[index]
            : population.m_entries[index].PlacementId);
        // Neither changes during a reconcile.
        public bool IsRemoteHuman(int index) => population.m_entries[index].IsRemoteHuman;
        public bool IsTransferred(int index) => population.m_entries[index].IsAuthorityTransferred;
        public int SimulatedCount {
            get => forecast.SimulatedCount;
            set => forecast.SimulatedCount = value;
        }
        public long CachedCount(int ordinal) => ((forecast.FreshCache || (ordinal >= forecast.CacheLength))
            ? NoInhabitCountObserved
            : population.m_inhabitCountCache[ordinal]);
        // A pass reads each ordinal's entry once, before it caches it, so the forecast keeps no cache of its own.
        public void CacheCount(int ordinal, long raw) { }
        public void SizeCountCache(int placements) {
            if (forecast.CacheLength != placements) {
                forecast.FreshCache = true;
                forecast.CacheLength = placements;
            }
        }
        public void InvalidateCountCache() => forecast.FreshCache = true;
        public void AdmitInhabitant(WorldDefinition definition, int index, WorldPlacement placement, WorldPlacementInhabit inhabit, byte kitIndex, int ordinal, int desired) {
            forecast.Copy(population: population);
            forecast.Active[index] = true;
            forecast.Placement[index] = placement.Id;
            forecast.Replaced[index] = true;
        }
        public void RetireInhabitant(int index) {
            forecast.Copy(population: population);
            forecast.Active[index] = false;
            forecast.Placement[index] = null;
            forecast.Replaced[index] = true;
        }
        public void BindInhabitant(int index, WorldPlacement placement, byte kitIndex) { }
        public void AdmitCensus(int index, bool reports) {
            forecast.Copy(population: population);
            forecast.Active[index] = true;
            forecast.Replaced[index] = true;
        }
        public void RetireCensus(int index, bool reports) {
            forecast.Copy(population: population);
            forecast.Active[index] = false;
            forecast.Replaced[index] = true;
        }
        public void TableFull(WorldPlacement placement) { }
        public void CountClamped(WorldPlacement placement, string row, int desired, int bound) { }
        public void Revise() { }
        public void Settle(WorldDefinition definition) { }
        public bool Keeps(int index) => (
            (((uint)index) >= ((uint)population.Capacity)) ||
            (forecast.Copied
                ? (forecast.Active[index] && !forecast.Replaced[index])
                : population.m_entries[index].Active)
        );
    }

    /// <summary>Reconciles the inhabited-body registrations against the delivered definition (the structural half of
    /// an install; <see cref="ReconcileInstall"/> runs it with the cell-driven half): a placement's inhabit facet joins
    /// bodies into the peer slice over the loopback link — an inhabitant is a <see cref="PopulationKind.NetworkPeer"/>
    /// whose entry carries a placement back-reference, holding a normal <see cref="WorldBody"/> under the resolved kit and
    /// driven by its kit's sensing steering producer. Bodies claim the highest free slots (capacity minus one downward)
    /// so an existing inhabitant never renumbers; admission is bounded only by the table itself and rejects loudly when
    /// it is genuinely full — there is no census-fit reservation. Diff-by-placement: retire an entry whose row vanished,
    /// lost its facet, or changed creation/kit; keep a matching one (its pose survives an unrelated placement edit);
    /// admit new bodies at the highest free slots — except a facet whose <see cref="WorldPlacementInhabit.Count"/> names
    /// a cell (<see cref="WorldPlacementInhabitCount.Row"/>), which this pass never grows: it starts at zero and is
    /// admitted only by <see cref="ReconcileInhabitCounts"/>, tracking the cell's own live value. The census ceiling
    /// (<see cref="MaxSimulated"/>) follows all non-census physical occupancy, and the census is re-clamped without
    /// renumbering an inhabitant or transferred entity.</summary>
    /// <param name="definition">The delivered definition (its placements, creations, kits, and look table).</param>
    /// <param name="admitted">Optional sink for the peer generations admitted by the reconciliation.</param>
    /// <param name="disconnected">Optional sink for the peer generations disconnected by the reconciliation.</param>
    public void ReconcileInhabitants(WorldDefinition definition, List<WorldPeerEventEntry>? admitted = null, List<WorldPeerEventEntry>? disconnected = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ReconcileStructure(
            definition: definition,
            slots: new LiveSlots(
                admitted: admitted,
                disconnected: disconnected,
                population: this
            )
        );
    }

    /// <summary>Reconciles every cell-driven inhabit facet's live count against its bound cell's current value —
    /// the value half of the count-cell primitive (<see cref="ReconcileInhabitants"/> is the structural half, and
    /// never grows a cell-referencing placement: such a placement starts at zero live bodies through every install,
    /// and is admitted here, which the server calls right after every structural reconcile, boot included, and on
    /// every state write). Skips a placement whose bound cell has not moved since the last call — this compares the
    /// resolved raw value against one cached long per tracked placement. A document with no cell-driven inhabit facet,
    /// or one whose cell simply has not changed this tick, walks a bounded array scan and allocates nothing.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="tick">The tick this reconcile answers as of.</param>
    /// <param name="admitted">Optional sink for the peer generations admitted by the reconciliation.</param>
    /// <param name="disconnected">Optional sink for the peer generations disconnected by the reconciliation.</param>
    /// <param name="arena">The live store, or declaration seeds during initial population construction.</param>
    public void ReconcileInhabitCounts(WorldDefinition definition, ulong tick, List<WorldPeerEventEntry>? admitted = null, List<WorldPeerEventEntry>? disconnected = null, StateArena? arena = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        _ = ReconcileCounts(
            arena: arena,
            definition: definition,
            slots: new LiveSlots(
                admitted: admitted,
                disconnected: disconnected,
                population: this
            ),
            tick: tick
        );
    }

    /// <summary>Reconciles the population an install of <paramref name="definition"/> delivers, after
    /// <see cref="Rebuild(WorldDefinition, WorldSolidField?)"/> (or at boot): the structural reconcile, then every
    /// cell-driven count from its cell, since the structural pass re-seeds them.</summary>
    /// <param name="definition">The delivered definition.</param>
    /// <param name="tick">The tick the cell-driven counts answer as of.</param>
    /// <param name="admitted">Sink for the peer generations admitted.</param>
    /// <param name="disconnected">Sink for the peer generations disconnected.</param>
    /// <param name="arena">The live store.</param>
    public void ReconcileInstall(WorldDefinition definition, ulong tick, List<WorldPeerEventEntry> admitted, List<WorldPeerEventEntry> disconnected, StateArena arena) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        Reconcile(
            arena: arena,
            definition: definition,
            slots: new LiveSlots(
                admitted: admitted,
                disconnected: disconnected,
                population: this
            ),
            structural: true,
            tick: tick
        );
    }

    /// <summary>Activates the first <paramref name="count"/> census stand-ins (indices <c>4..</c>), clamped to
    /// <c>0..</c><see cref="MaxSimulated"/>, and deactivates the rest. A newly-activated
    /// entry is re-seeded to a fresh spawn and given its own <see cref="WorldBody"/> (a server-authoritative spawn at that
    /// pose); a deactivated entry drops its body; entries already active keep running their producers. Bumps the revision only when an
    /// occupancy flips.</summary>
    /// <param name="count">The requested active census count.</param>
    /// <param name="admitted">Optional sink for the peer generations admitted by the census change.</param>
    /// <param name="disconnected">Optional sink for the peer generations disconnected by the census change.</param>
    /// <returns>The clamped count actually applied.</returns>
    public int SetSimulatedCount(int count, List<WorldPeerEventEntry>? admitted = null, List<WorldPeerEventEntry>? disconnected = null) => SelectCensus(
        count: count,
        reports: true,
        slots: new LiveSlots(
            admitted: admitted,
            disconnected: disconnected,
            population: this
        )
    );

    /// <summary>Whether every body in <paramref name="bodies"/> is the same body, still active, in the population the
    /// reconcile of <paramref name="definition"/> and <paramref name="arena"/> produces: the reconcile an install runs,
    /// forecast over a scratch copy of the slot table without changing anything.</summary>
    /// <param name="definition">The document the reconcile reads, such as one a firing's document rows compose.</param>
    /// <param name="arena">The store the cell-driven counts read, such as one with a firing's scope open.</param>
    /// <param name="tick">The tick the cell-driven counts answer as of.</param>
    /// <param name="structural">Whether the install rebuilds the population, so the structural reconcile runs before
    /// the cell-driven counts; otherwise only the counts settle.</param>
    /// <param name="bodies">The body indices.</param>
    /// <returns><see langword="false"/> when the reconcile would retire or replace one of them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public bool ReconcileKeeps(WorldDefinition definition, StateArena? arena, ulong tick, bool structural, ReadOnlySpan<int> bodies) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var forecast = (m_forecast ??= new SlotForecast());

        forecast.Begin(population: this);

        var slots = new ForecastSlots(
            forecast: forecast,
            population: this
        );

        Reconcile(
            arena: arena,
            definition: definition,
            slots: slots,
            structural: structural,
            tick: tick
        );

        foreach (var body in bodies) {
            if (!slots.Keeps(index: body)) {
                return false;
            }
        }

        return true;
    }

    // An install's reconcile: the structural pass when the install rebuilds the population, then the cell-driven counts.
    private void Reconcile<TSlots>(TSlots slots, WorldDefinition definition, StateArena? arena, ulong tick, bool structural) where TSlots : struct, ISlots {
        if (structural) {
            ReconcileStructure(
                definition: definition,
                slots: slots
            );
        }

        _ = ReconcileCounts(
            arena: arena,
            definition: definition,
            slots: slots,
            tick: tick
        );
    }

    private void ReconcileStructure<TSlots>(TSlots slots, WorldDefinition definition) where TSlots : struct, ISlots {
        // Pass 1 — retire inhabited slots whose placement/facet/creation-kit binding no longer holds. A surviving slot
        // keeps its body (pose preserved); a kit change recompiles in place. An inhabited entry is a peer carrying a
        // placement back-reference; a plain census peer (no PlacementId) is left untouched.
        for (var index = (Capacity - 1); (index >= LocalSeatCount); index--) {
            if (slots.PlacementOf(index: index) is not { } placementId) {
                continue;
            }

            if (!TryFindBindable(
                definition: definition,
                kitIndex: out var kitIndex,
                placement: out var placement,
                placementId: placementId
            )) {
                slots.RetireInhabitant(index: index);

                continue;
            }

            slots.BindInhabitant(
                index: index,
                kitIndex: kitIndex,
                placement: placement
            );
        }

        // Pass 2 — grow/shrink each LITERAL-count inhabited placement to its declared count, at the highest free
        // slots (document order). A cell-driven count (Count.Row set) is skipped here on purpose: it starts at
        // zero live bodies through every structural install (boot included) and is grown/shrunk exclusively by
        // ReconcileCounts, gated on the bound cell's own value actually moving — a structural edit that leaves the
        // cell untouched must not silently re-fan a live census.
        foreach (var placement in definition.Placements) {
            if (
                (placement.Inhabit is not { } inhabit) ||
                (inhabit.Count?.Row is not null) ||
                (InhabitKit(
                definition: definition,
                placement: placement
            ) is not { } kitIndex)
            ) {
                continue;
            }

            ReconcileOneInhabitedCount(
                definition: definition,
                desired: LiteralInhabitants(inhabit: inhabit),
                inhabit: inhabit,
                kitIndex: kitIndex,
                placement: placement,
                slots: slots
            );
        }

        // Re-clamp the census against every entity-table slot now owned by an inhabitant or transferred authority:
        // a freed slot below a census body takes the census ahead of it, which retires the highest census body.
        _ = SelectCensus(
            count: slots.SimulatedCount,
            reports: false,
            slots: slots
        );
        // A structural install can reorder or replace placements at a fixed ordinal without changing their count
        // (ReconcileCounts' own cache resize gate misses that), so every structural pass invalidates the whole cache
        // — the next cell-driven reconcile re-resolves each cell-driven facet fresh rather than trusting a raw value
        // that may now belong to a different row entirely.
        slots.InvalidateCountCache();
        slots.Settle(definition: definition);
    }

    private bool ReconcileCounts<TSlots>(TSlots slots, WorldDefinition definition, StateArena? arena, ulong tick) where TSlots : struct, ISlots {
        var placements = definition.Placements;

        slots.SizeCountCache(placements: placements.Count);

        var changed = false;

        for (var ordinal = 0; (ordinal < placements.Count); ordinal++) {
            var placement = placements[ordinal];

            if (
                (placement.Inhabit is not { } inhabit) ||
                (inhabit.Count?.Row is not { } row) ||
                (InhabitKit(
                definition: definition,
                placement: placement
            ) is not { } kitIndex)
            ) {
                continue;
            }

            var raw = (inhabit.Count.Resolve(arena: arena,
                definition: definition,
                tick: tick
            ) ?? 0L);

            if (raw == slots.CachedCount(ordinal: ordinal)) {
                continue;
            }

            slots.CacheCount(
                ordinal: ordinal,
                raw: raw
            );
            changed = true;

            var sampleCount = ((inhabit.Distribution?.Region as WorldDistributionRegion.Disc)?.SampleCount);
            var bound = Math.Min(
                val1: PeerCapacity,
                val2: (sampleCount ?? PeerCapacity)
            );
            var desired = ((int)Math.Clamp(
                max: ((long)bound),
                min: 0L,
                value: raw
            ));

            if (desired != raw) {
                slots.CountClamped(
                    bound: bound,
                    desired: desired,
                    placement: placement,
                    row: row
                );
            }

            ReconcileOneInhabitedCount(
                definition: definition,
                desired: desired,
                inhabit: inhabit,
                kitIndex: kitIndex,
                placement: placement,
                slots: slots
            );
        }

        if (changed) {
            _ = SelectCensus(
                count: slots.SimulatedCount,
                reports: false,
                slots: slots
            );
            slots.Settle(definition: definition);
        }

        return changed;
    }

    // Grows or shrinks one inhabited placement's live census to `desired`: growth claims the highest free slots in
    // document order (HighestFreeSlot's own remarks — the first body admitted always lands at the highest index), and
    // shrink retires the lowest surviving index that is not currently a seat's own claimed body (Entry.IsRemoteHuman)
    // — since admission always fills the highest slot first, the lowest index is always the first one admitted, and
    // skipping a human-occupied one here is what keeps a possessed inhabitant standing through a shrink instead of
    // being torn out from under its player. Shared by the structural (literal-count) pass and the cell-driven counts.
    private void ReconcileOneInhabitedCount<TSlots>(TSlots slots, WorldDefinition definition, WorldPlacement placement, WorldPlacementInhabit inhabit, byte kitIndex, int desired) where TSlots : struct, ISlots {
        var live = CountInhabitants(
            placementId: placement.Id,
            slots: slots
        );

        for (var ordinal = live; (ordinal < desired); ordinal++) {
            var slot = HighestFreeSlot(slots: slots);

            if (slot < 0) {
                slots.TableFull(placement: placement);

                return;
            }

            slots.AdmitInhabitant(
                definition: definition,
                desired: desired,
                index: slot,
                inhabit: inhabit,
                kitIndex: kitIndex,
                ordinal: ordinal,
                placement: placement
            );
        }

        for (var extra = desired; (extra < live); extra++) {
            var slot = LowestRetirableInhabitant(
                placementId: placement.Id,
                slots: slots
            );

            if (slot < 0) {
                // Every remaining inhabitant is seat-driven — nothing left this pass can retire.
                return;
            }

            slots.RetireInhabitant(index: slot);
        }
    }

    // The census selection: the first `count` eligible slots in ascending order, clamped to the eligible slots.
    // Inhabited and authority-transferred slots are owned by their own lifecycles. They may occupy any index after a
    // mapped handoff, so census selection counts eligible slots instead of assuming all exclusions live at the top of
    // the table. The networkPlayers cap counts humans only.
    //
    // Only a requested census change (SetSimulatedCount) reports peer lifecycle events. An install's re-clamps report
    // none: the structural and count passes share one install's event lists, so a census body the first re-clamp admits
    // and the second retires would leave both an admission and a disconnect, and delivery
    // (WorldGrants.ApplyLifecycleEvents) applies every disconnect before every admission, which would bring the
    // retired body back and raise the census.
    private int SelectCensus<TSlots>(TSlots slots, int count, bool reports) where TSlots : struct, ISlots {
        var clamped = Math.Clamp(
            value: count,
            min: 0,
            max: AvailableCensusSlots(slots: slots)
        );
        var changed = false;
        var remaining = clamped;

        for (var offset = 0; (offset < PeerCapacity); offset++) {
            var index = (LocalSeatCount + offset);

            if (!IsCensusSlot(
                index: index,
                slots: slots
            )) {
                continue;
            }

            var desired = (remaining > 0);

            if (desired) {
                remaining--;
            }

            if (slots.IsActive(index: index) == desired) {
                continue;
            }

            if (desired) {
                slots.AdmitCensus(
                    index: index,
                    reports: reports
                );
            } else {
                slots.RetireCensus(
                    index: index,
                    reports: reports
                );
            }

            changed = true;
        }

        slots.SimulatedCount = clamped;

        if (changed) {
            slots.Revise();
        }

        return clamped;
    }
    // A slot the census may occupy: no inhabitant, human, transferred authority or held body owns it.
    private bool IsCensusSlot<TSlots>(TSlots slots, int index) where TSlots : struct, ISlots => (
        (slots.PlacementOf(index: index) is null) &&
        !slots.IsRemoteHuman(index: index) &&
        !slots.IsTransferred(index: index) &&
        (HeldPlacement(
            index: index,
            slots: slots
        ) is null)
    );
    private int AvailableCensusSlots<TSlots>(TSlots slots) where TSlots : struct, ISlots {
        var count = 0;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (IsCensusSlot(
                index: index,
                slots: slots
            )) {
                count++;
            }
        }
        return count;
    }
    // What holds an empty index for a body that may come back to it, or null when nothing does (Holds).
    private string? HeldPlacement<TSlots>(TSlots slots, int index) where TSlots : struct, ISlots => ((!slots.IsActive(index: index) && (Holds is { } holds))
        ? holds(arg: index)
        : null);
    // A placement's inhabitants: its bodies here, and the held slots of its bodies away, which a reconcile must not
    // replace while their titles may bring them back.
    private int CountInhabitants<TSlots>(TSlots slots, string placementId) where TSlots : struct, ISlots {
        var count = 0;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            var owner = slots.PlacementOf(index: index);

            if (
                string.Equals(
                a: owner,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            ) ||
                ((owner is null) && string.Equals(a: HeldPlacement(index: index, slots: slots), b: placementId, comparisonType: StringComparison.Ordinal))
            ) {
                count++;
            }
        }

        return count;
    }
    // The highest slot (capacity minus one downward) not currently claimed by an active seat/census peer or an inhabited
    // peer — where a new inhabited body lands, so inhabitants cluster at the top and never renumber an existing peer. A
    // free slot is one that holds no placement back-reference, no active body and no held body.
    private int HighestFreeSlot<TSlots>(TSlots slots) where TSlots : struct, ISlots {
        for (var index = (Capacity - 1); (index >= LocalSeatCount); index--) {
            if (
                (slots.PlacementOf(index: index) is null) &&
                !slots.IsActive(index: index) &&
                (HeldPlacement(
                index: index,
                slots: slots
            ) is null)
            ) {
                return index;
            }
        }

        return -1;
    }
    // The shrink's retire order: the lowest-index inhabitant of the placement that is not a seat's own claimed body,
    // so a possessed inhabitant is never retired out from under its player.
    private int LowestRetirableInhabitant<TSlots>(TSlots slots, string placementId) where TSlots : struct, ISlots {
        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (
                !slots.IsRemoteHuman(index: index) &&
                string.Equals(
                a: slots.PlacementOf(index: index),
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return index;
            }
        }

        return -1;
    }
    private LiveSlots Live() => new(
        admitted: null,
        disconnected: null,
        population: this
    );
    private int AvailableCensusSlots() => AvailableCensusSlots(slots: Live());
    private int HighestFreeSlot() => HighestFreeSlot(slots: Live());
}
