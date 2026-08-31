using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    private static readonly Func<WorldKit, string> KitRowName = static kit => kit.Name;
    private static readonly Func<WorldLook, string> LookRowName = static look => look.Name;

    // Compile the definition's sim-affecting sections to the fixed-point tables runtime simulation reads: the profileless
    // motion tuning, kit producer parameters, kit rows and their fixed compilations, and the resolved seat-kit row. Shared by the
    // constructor and Rebuild so a live retune quantizes through exactly the same path.
    private void CompileFixedTables(WorldDefinition definition, WorldSolidField? solids) {
        LocalSeatCount = definition.Population.LocalSeats;
        var authoredMotion = definition.Motion;

        m_fixedMotion = WorldMotionTuningFactory.Compile(motion: in authoredMotion);
        m_playerDefaults = definition.PlayerDefaults;
        m_peerVariation = definition.Population.PeerVariation;
        m_seatVariation = definition.Population.SeatVariation;
        m_peerColors = definition.Population.PeerColors;
        m_reconnectGraceTicks = definition.PopulationReconnectGraceTicks;
        m_kitRows = definition.Kits;
        var programs = new Dictionary<string, CompiledBodyMotionProgram>(comparer: StringComparer.Ordinal);
        var programRows = new Dictionary<string, BodyMotionProgram>(comparer: StringComparer.Ordinal);

        foreach (var program in definition.BodyMotionPrograms) {
            programs.Add(
                key: program.Name,
                value: BodyMotionProgramFactory.Compile(program: program)
            );
            programRows.Add(
                key: program.Name,
                value: program
            );
        }
        m_bodyMotionPrograms = programs;
        m_channels = WorldChannelTable.Compile(channels: definition.Channels);
        m_fixedAttachment = FixedWorldAttachment.Compile(
            channels: m_channels,
            section: definition.Attachment
        );
        m_targetRows = definition.TargetRegisters;
        m_targets = WorldTargetRegisterTable.Compile(
            registers: definition.TargetRegisters,
            channelCount: m_channels.ChannelCount
        );
        // The LIVE row list — a curve-follow producer's per-tick Evaluate reads m_curveRows[CurveIndex].Compiled
        // straight off this reference, so a curve upsert (which reassigns definition.Curves and re-runs Rebuild)
        // retargets every follower on delivery without a second per-tick indirection.
        m_curveRows = definition.Curves;
        m_curves = WorldCurveTable.Compile(curves: definition.Curves);
        m_kits = new FixedWorldKit[definition.Kits.Count];

        for (var kit = 0; (kit < m_kits.Length); kit++) {
            m_kits[kit] = FixedWorldKit.Compile(
                kit: definition.Kits[kit],
                channels: m_channels,
                targets: m_targets,
                curves: m_curves,
                programs: m_bodyMotionPrograms,
                programRows: programRows,
                creations: definition.Creations,
                bodyState: definition.BodyState,
                identityState: definition.IdentityState,
                dynamics: definition.Dynamics,
                simulationRateHz: definition.SimulationRateHz
            );
        }

        // Derive the contact field the definition selects — the ONE derivation both a fresh activation and a live body
        // read. The field provider's program is handed in pre-built at runtime; at boot it is compiled here.
        m_contactCensus = WorldColliderSet.Measure(definition: definition);
        // The gravitational field the definition authors. Compiled here beside the contact field so one derivation
        // serves a fresh activation and a live body alike.
        m_gravityField = new WorldGravityField(
            capacity: Capacity,
            compiled: FixedWorldGravity.Compile(
                gravity: definition.Gravity,
                placements: definition.Placements
            )
        );
        var derivedSolids = solids;

        // Field state exists independently of the selected contact/target provider. A world may use its lattice only
        // for reactions, exposure rows, snapshots, or rendering and still owes the same authoritative state.
        InstallFields(definition: definition);

        if (
            (derivedSolids is null) &&
            (WorldContactSelection.RequiresField(collision: definition.Collision) || WorldTargetSelection.RequiresLineOfSight(definition: definition))
        ) {
            if (!WorldSolidField.TryBuild(
                built: out derivedSolids,
                definition: definition,
                reason: out var reason,
                lattice: m_fields
            )) {
                throw new InvalidOperationException(message: $"the target/contact field could not compile the world's solids at boot: {reason}");
            }
        }
        m_baseContactField = ResolveContactField(
            definition: definition,
            solids: derivedSolids
        );
        m_adjacencyDefinition = definition;
        ComposeContactField();
        m_targetField = (WorldTargetSelection.RequiresLineOfSight(definition: definition)
            ? derivedSolids
            : null
        );
        m_seatKit = ResolveKit(name: definition.DefaultSeatKit);
        // The LOOK table: the authored rows, or the implicit single catalog look when the author declared none.
        m_lookRows = WorldDefinitionRows.ResolveLookRows(looks: definition.Looks);
        // The compiled population distribution — read ONLY by SeedSimulated (never the authored floats). The validator has already
        // resolved every named spawn point, so Compile's lookups always hit.
        m_distribution = FixedWorldDistribution.Compile(
            distribution: definition.Population.Distribution,
            spawnPoints: definition.SpawnPoints
        );
        m_lookAssignment = definition.LookAssignment;
        // The remote admission cap moves with the live document (a swap can raise or lower networkPlayers); the running
        // census count is re-clamped against it by ReconcileInhabitants' trailing SetSimulatedCount.
        m_remoteCap = definition.Population.NetworkPlayers;
    }

    /// <summary>Checks whether the candidate's field runtime can replace the live reaction plan while preserving
    /// the boot-allocated lattice cells.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <param name="reason">The named incompatibility on refusal; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the candidate can retain the live field allocation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    public bool CanInstallFields(WorldDefinition definition, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!m_fieldsCompiled) {
            reason = null;

            return true;
        }

        var document = definition.Fields;
        var program = definition.FieldProgram;

        if ((document is null) != (program is null)) {
            reason = "the definition's field composite and compiled program disagree";

            return false;
        }

        if ((m_fields is null) != (document is null)) {
            reason = "adding or removing the live field lattice changes its allocation; restart the host to load it";

            return false;
        }

        if (m_fields is null) {
            reason = null;

            return true;
        }

        return m_fields.CanInstallProgram(
            document: document!,
            program: program!,
            reason: out reason
        );
    }

    /// <summary>Installs the candidate's compatible field companion/program pair, preserving live cell state.</summary>
    /// <param name="definition">The candidate definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The candidate changes the live field allocation.</exception>
    public void InstallFields(WorldDefinition definition) {
        if (!CanInstallFields(
            definition: definition,
            reason: out var reason
        )) {
            throw new InvalidOperationException(message: reason);
        }

        if (!m_fieldsCompiled) {
            m_fields = ((definition.Fields is { } document)
                ? new WorldFieldLattice(
                    document: document,
                    program: (definition.FieldProgram ?? throw new InvalidOperationException(message: "The field composite has no compiled program.")),
                    worldSeed: (definition.Generation?.WorldSeed ?? 0UL)
                )
                : null
            );
            m_fieldsCompiled = true;
        } else if (m_fields is { } fields) {
            fields.InstallProgram(
                document: definition.Fields!,
                program: definition.FieldProgram!
            );
        }
    }
    private static FixedSpawnPoint[] CompileSeatSpawns(IReadOnlyList<WorldSpawnPoint> spawnPoints, IReadOnlyList<string> seatSpawns) {
        var compiled = new FixedSpawnPoint[seatSpawns.Count];

        for (var index = 0; (index < compiled.Length); index++) {
            var point = WorldDefinitionRows.FindSpawnPoint(
                spawnPoints: spawnPoints,
                id: seatSpawns[index]
            )!.Value;

            compiled[index] = FixedSpawnPoint.Compile(point: in point);
        }

        return compiled;
    }
    // Recomposes m_contactField from the current base field, adjacency source, and live definition — the
    // ONE place any of the three changes. A definition authoring no adjacency, or no injected source, leaves
    // m_contactField pointing at m_baseContactField directly.
    private void ComposeContactField() {
        if (
            (m_baseContactField is not { } baseField) ||
            (m_adjacencyDefinition is not { } definition)
        ) {
            m_contactField = m_baseContactField;

            return;
        }

        var bands = ((m_adjacencies is not null)
            ? WorldAdjacencyBands.CollectFrom(definition: definition)
            : []
        );

        m_contactField = (((m_adjacencies is { } source) && (bands.Count > 0))
            ? new WorldAdjacencyContactField(
                inner: baseField,
                source: source
            )
            : baseField
        );
    }
    // The requirements-selected contact field: the analytic convex-collider set when no field quality is required, or
    // the pre-built SDF field otherwise. At runtime the
    // server hands the pre-built field (built once at apply time for its loud excluded-op rejection); at boot (solids ==
    // null) the field is compiled here and a bad-op world fails loudly.
    private static IContactField? ResolveContactField(WorldDefinition definition, WorldSolidField? solids) {
        if (WorldContactSelection.RequiresField(collision: definition.Collision)) {
            if (solids is not null) {
                return solids;
            }

            return (solids ?? throw new InvalidOperationException(message: "the field contact provider was not compiled."));
        }

        return WorldColliderSet.Build(definition: definition);
    }
    // The kit name an inhabited placement resolves: its explicit Inhabit.Kit, or the creation's Locomotion token as a
    // kit name (the creator's rule). Null when neither resolves to a string (the validator already rejected such a row).
    private static string? ResolveInhabitKit(WorldDefinition definition, WorldPlacement placement) {
        if (placement.Inhabit?.Kit is { Length: > 0 } explicitKit) {
            return explicitKit;
        }

        foreach (var creation in definition.Creations) {
            if (string.Equals(
                a: creation.Id,
                b: placement.PrototypeId,
                comparisonType: StringComparison.Ordinal
            )) {
                return creation.Document.Behavior?.Locomotion;
            }
        }

        return null;
    }
    // The look row an inhabited placement's bodies wear: its Inhabit.Look when it names an authored look, else the
    // implicit index-derived look (the client renders the creation stamp from the placement's own PrototypeId regardless).
    private byte ResolveInhabitLook(WorldPlacement placement) {
        if (
            (placement.Inhabit?.Look is { Length: > 0 } lookName) &&
            (ResolveLookOrNull(name: lookName) is { } lookIndex)
        ) {
            return lookIndex;
        }

        return SelectRow(
            index: 0,
            assignment: m_lookAssignment,
            rows: m_lookAssignmentRows,
            rowCount: m_lookRows.Count
        );
    }
    // The kit row index a kebab name resolves to. The validator gates unknown names at startup, EXCEPT the derived
    // empty case: no kits declared resolves defaultSeatKit to "", and the validator admits that only when capacity
    // is zero too — so no entry ever exists to read the sentinel this returns.
    private byte ResolveKit(string name) {
        if (m_kitRows.Count == 0) {
            return 0;
        }

        return (ResolveKitOrNull(name: name)
            ?? throw new InvalidOperationException(message: $"No kit row named '{name}' in the world definition."));
    }
    // The kit row a population index actually runs: a local seat (0..LocalSeatCount) always reads the resolved seat
    // kit (m_seatKit), never its entry's own KitIndex — the seat kit can differ from a seat entry's assigned row on a
    // multi-kit world. Every seat-vs-peer kit read (recompile, producer-support checks, kit-replace safety, and the
    // runtime coherence door) shares this ONE resolver so they can never disagree.
    private byte ResolveKitIndex(int index) => ((index < LocalSeatCount)
        ? m_seatKit
        : m_entries[index].KitIndex
    );
    private byte? ResolveKitOrNull(string name) => ResolveRowIndex(
        name: name,
        nameOf: KitRowName,
        rows: m_kitRows
    );
    // The look row index a kebab name resolves to. The validator gates unknown names at startup / apply.
    private byte ResolveLook(string name) => (ResolveLookOrNull(name: name)
        ?? throw new InvalidOperationException(message: $"No look row named '{name}' in the world definition."));
    // Resolve every entry's LookIndex from the definition's authored sequence and row view.
    private void ResolveLookIndices(WorldDefinition definition) {
        m_lookAssignmentRows = ResolveRows(
            assignment: definition.LookAssignment,
            resolve: ResolveLook
        );

        for (var index = 0; (index < Capacity); index++) {
            m_entries[index].LookIndex = SelectRow(
                index: index,
                assignment: definition.LookAssignment,
                rows: m_lookAssignmentRows,
                rowCount: m_lookRows.Count
            );
        }
    }
    private byte? ResolveLookOrNull(string name) => ResolveRowIndex(
        name: name,
        nameOf: LookRowName,
        rows: m_lookRows
    );
    // The one ordinal name search every named row table resolves through, so a kit lookup and a look lookup can never
    // disagree on what "the same name" means. Cached nameOf delegates keep the call allocation-free.
    private static byte? ResolveRowIndex<TRow>(IReadOnlyList<TRow> rows, string name, Func<TRow, string> nameOf) {
        for (var row = 0; (row < rows.Count); row++) {
            if (string.Equals(
                a: nameOf(arg: rows[row]),
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return ((byte)row);
            }
        }

        return null;
    }
    // An authored row view resolved to table indices. Empty means every declared row in declaration order.
    private static byte[]? ResolveRows(WorldRowAssignment assignment, Func<string, byte> resolve) {
        if (assignment.Rows.Count == 0) {
            return null;
        }

        var table = new byte[assignment.Rows.Count];

        for (var entry = 0; (entry < table.Length); entry++) {
            table[entry] = resolve(arg: assignment.Rows[entry]);
        }

        return table;
    }
    private static byte SelectRow(int index, WorldRowAssignment assignment, byte[]? rows, int rowCount) {
        var sourceCount = (rows?.Length ?? rowCount);
        var selected = WorldSequenceSampling.Bucket(
            sequence: assignment.Sequence,
            index: index,
            count: sourceCount
        );

        return ((rows is null)
            ? (byte)selected
            : rows[selected]
        );
    }

    /// <summary>Checks whether replacing a kit would orphan a producer selected by a live body.</summary>
    /// <param name="replacement">The proposed kit row.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when every affected live source remains declared.</returns>
    public bool CanReplaceKit(WorldKit replacement, out string refusal) {
        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];
            var selectedKit = ResolveKitIndex(index: index);

            if (
                (entry is { Active: true, Body: { } body }) &&
                string.Equals(
                a: m_kitRows[selectedKit].Name,
                b: replacement.Name,
                comparisonType: StringComparison.Ordinal
            ) &&
                (body.Source.ProducerName is { } producerName) &&
                !replacement.Producers.ContainsKey(key: producerName)
            ) {
                refusal = $"body {(index + 1)} selects producer '{producerName}' from kit '{replacement.Name}'";

                return false;
            }
        }

        refusal = string.Empty;

        return true;
    }
    /// <summary>Configures (or clears) the adjacency source every live body's contact resolution consults inside an
    /// overlap — see
    /// <see cref="WorldServer.Adjacencies"/>, the one writer. Recomposes <see cref="m_contactField"/> immediately
    /// against the current definition/base field, without rebuilding either — a border resolver becoming reachable
    /// (or unreachable) never itself re-derives the world's own solid geometry.</summary>
    /// <param name="source">The resolver, or <see langword="null"/> to fall back to this world's own geometry alone.</param>
    public void ConfigureAdjacencies(IWorldAdjacencySource? source) {
        if (ReferenceEquals(
            objA: m_adjacencies,
            objB: source
        )) {
            return;
        }

        m_adjacencies = source;
        ComposeContactField();

        // The composition root configures the runtime adjacency source after the boot seats already exist. Bodies retain
        // their own field reference, so recomposing only the population's field would leave those live bodies on the
        // old base field forever. Hand the effective field to every live body on the same terms as Rebuild; pose,
        // velocity, intent, and every other body property remain untouched.
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is { Active: true, Body: { } body }) {
                body.SetContactField(field: m_contactField);
                body.SetGravityField(field: m_gravityField);
            }
        }
    }
    /// <summary>Recompiles the population's derived state after a sim-affecting section mutation (a live kit tune, a
    /// motion/wander retune, a seat-kit or assignment change, or a whole-document swap): re-quantizes the fixed tables,
    /// re-resolves every entry's kit index, re-derives the kit/wander-dependent per-entry statics without resetting the
    /// running wander phase, and swaps every live body's compiled tuning/actions/program in place — bodies keep their
    /// pose/velocity/tape, only the compiled feel swaps. Bumps <see cref="Revision"/> so the client rebuilds the avatar
    /// program. New activations re-seed fully from these fresh tables.</summary>
    /// <param name="definition">The new live definition.</param>
    /// <param name="solids">The server's pre-built SDF contact field for the field provider (built once at apply time so
    /// a runtime edit never rebuilds it twice), or <see langword="null"/> under the analytic provider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The candidate changes the boot-allocated field lattice shape,
    /// topology, cadence, or field envelope.</exception>
    public void Rebuild(WorldDefinition definition, WorldSolidField? solids) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!CanInstallFields(
            definition: definition,
            reason: out var fieldReason
        )) {
            throw new InvalidOperationException(message: fieldReason);
        }

        m_seatSpawns = CompileSeatSpawns(
            spawnPoints: definition.SpawnPoints,
            seatSpawns: definition.Population.SeatSpawns
        );

        var priorTargets = m_targets;

        CompileFixedTables(
            definition: definition,
            solids: solids
        );

        for (var bodyIndex = 0; (bodyIndex < m_entries.Length); bodyIndex++) {
            var prior = m_entries[bodyIndex].Designations;
            var current = NewDesignations();

            for (var priorIndex = 0; (priorIndex < prior.Length); priorIndex++) {
                if (m_targets.TryGetIndex(
                    name: priorTargets.Name(index: priorIndex),
                    index: out var currentIndex
                )) {
                    current[currentIndex] = prior[priorIndex];
                }
            }

            m_entries[bodyIndex].Designations = current;
        }

        var assignmentRows = ResolveRows(
            assignment: definition.Assignment,
            resolve: ResolveKit
        );

        for (var index = 0; (index < Capacity); index++) {
            m_entries[index].KitIndex = SelectRow(
                index: index,
                assignment: definition.Assignment,
                rows: assignmentRows,
                rowCount: m_kits.Length
            );
        }

        // Re-resolve the look table too — a live look row/assignment mutation flows through Rebuild (AffectsRenderEnvelope
        // + the client program rebuild the bumped revision triggers). PRESENTATION-ONLY, so it touches no body state.
        ResolveLookIndices(definition: definition);

        // Re-derive the kit/wander-dependent per-entry statics from the fresh tables, but keep the running wander phase
        // (resetPhase: false) so the live crowd's producer stays continuous — no phase jerk on a retune.
        for (var index = LocalSeatCount; (index < Capacity); index++) {
            SeedSimulated(
                index: index,
                resetPhase: false
            );
        }

        for (var slot = 0; (slot < LocalSeatCount); slot++) {
            if (m_entries[slot].Active) {
                SeedSeatWander(
                    resetPhase: false,
                    slot: slot
                );
            }
        }

        // Swap every live body's compiled feel in place; the seat bodies read the (possibly new) seat kit, peers read
        // their reassigned kit index. Pose/velocity/tape/source survive; only the compiled tuning/actions/program change.
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is not { Active: true, Body: { } body }) {
                continue;
            }

            var kitIndex = ResolveKitIndex(index: index);
            var kit = m_kits[kitIndex];

            body.RecompileKit(
                motion: m_kitRows[kitIndex].Motion,
                actions: kit.Actions,
                actionThresholds: kit.ActionThresholds,
                actionShapes: kit.ActionShapes,
                roleMask: kit.RoleMask,
                roleOrdinals: kit.RoleOrdinals,
                actionState: kit.ActionState,
                program: kit.BodyMotionProgram,
                programs: m_bodyMotionPrograms,
                collider: kit.Collider,
                maxSmoothError: m_fixedMotion.MaxSmoothError,
                sprintChannelOrdinal: kit.SprintChannelOrdinal,
                driftChannelOrdinal: kit.DriftChannelOrdinal,
                planarDynamics: kit.PlanarDynamics
            );
            // Hand the (possibly rebuilt) contact field to every live body, so a live solid-geometry or collision-tuning
            // edit takes effect on the next tick.
            body.SetContactField(field: m_contactField);
            body.SetGravityField(field: m_gravityField);
            body.SetAttachmentPolicy(policy: m_fixedAttachment);
        }

        m_revision++;
    }
    /// <summary>
    /// Resolves active local body pairs after every body has integrated. Pair order is stable population-index order;
    /// each body's own authority remains its sole pose writer and an overlap is shared equally between the pair.
    /// </summary>
    public void ResolveDynamicContacts() {
        var two = FixedQ4816.FromInteger(value: 2L);
        Span<int> indices = stackalloc int[WorldBodiesLimits.CapacityCeiling];
        Span<FixedQ4816> minimumX = stackalloc FixedQ4816[WorldBodiesLimits.CapacityCeiling];
        Span<FixedQ4816> maximumX = stackalloc FixedQ4816[WorldBodiesLimits.CapacityCeiling];
        Span<FixedQ4816> radii = stackalloc FixedQ4816[WorldBodiesLimits.CapacityCeiling];
        var count = 0;

        for (var index = 0; (index < Capacity); index++) {
            if (
                !m_entries[index].Active ||
                (BodyContact(index: index) != WorldBodyContactMode.Solid) ||
                (m_entries[index].Body is not { Collider: { } collider, OrdinaryAdvanceAdmitted: true } body)
            ) {
                continue;
            }

            var radius = FixedDynamicBodyContacts.BroadphaseRadius(volumes: collider.Volumes);

            indices[count] = index;
            minimumX[count] = (body.FixedPosition.X - radius);
            maximumX[count] = (body.FixedPosition.X + radius);
            radii[count] = radius;
            count++;
        }

        // Stable insertion sort: the table is tiny (<=128), already nearly ordered between ticks, and this avoids a
        // per-tick allocation. Population index is the complete tie-breaker, so replay cannot depend on sort quirks.
        for (var index = 1; (index < count); index++) {
            var bodyIndex = indices[index];
            var min = minimumX[index];
            var max = maximumX[index];
            var radius = radii[index];
            var destination = index;

            while (
                (destination > 0) &&
                ((minimumX[(destination - 1)] > min) ||
                ((minimumX[(destination - 1)] == min) && (indices[(destination - 1)] > bodyIndex)))
            ) {
                indices[destination] = indices[(destination - 1)];
                minimumX[destination] = minimumX[(destination - 1)];
                maximumX[destination] = maximumX[(destination - 1)];
                radii[destination] = radii[(destination - 1)];
                destination--;
            }
            indices[destination] = bodyIndex;
            minimumX[destination] = min;
            maximumX[destination] = max;
            radii[destination] = radius;
        }

        DynamicContactPotentialPairs = ((count * (count - 1)) / 2);
        DynamicContactNarrowPairs = 0;
        DynamicContactResolvedPairs = 0;

        for (var leftOrdinal = 0; (leftOrdinal < count); leftOrdinal++) {
            var leftIndex = indices[leftOrdinal];
            var left = m_entries[leftIndex].Body!;
            var leftCollider = left.Collider!.Value;

            for (var rightOrdinal = (leftOrdinal + 1); ((rightOrdinal < count) && (minimumX[rightOrdinal] <= maximumX[leftOrdinal])); rightOrdinal++) {
                var rightIndex = indices[rightOrdinal];
                var right = m_entries[rightIndex].Body!;
                var rightCollider = right.Collider!.Value;
                var radius = (radii[leftOrdinal] + radii[rightOrdinal]);
                var delta = (left.FixedPosition - right.FixedPosition);

                if (
                    (FixedQ4816.Abs(value: delta.Y) > radius) ||
                    (FixedQ4816.Abs(value: delta.Z) > radius)
                ) {
                    continue;
                }

                DynamicContactNarrowPairs++;

                if (FixedDynamicBodyContacts.TryCorrection(
                    leftPosition: left.FixedPosition,
                    leftOrientation: left.FixedOrientation,
                    leftVolumes: leftCollider.Volumes,
                    rightPosition: right.FixedPosition,
                    rightOrientation: right.FixedOrientation,
                    rightVolumes: rightCollider.Volumes,
                    tieBreaker: leftIndex ^ rightIndex,
                    correction: out var correction
                )) {
                    var shared = (correction / two);

                    left.ApplyDynamicContact(correction: shared);
                    right.ApplyDynamicContact(correction: -shared);
                    DynamicContactResolvedPairs++;
                }
            }
        }
    }
    /// <summary>Resolves every attached tether after every body has integrated and dynamic contacts have resolved —
    /// the same "late correction over the whole population's current-tick pose" slot <see cref="ResolveDynamicContacts"/>
    /// occupies, so a body-anchored tether reads its anchor's just-advanced pose rather than one tick stale. One-way by
    /// construction: <see cref="WorldBody.SolveTether"/> only ever writes the tethered body, never the anchor — an
    /// anchor body whose own entry is inactive or out of range this tick leaves its tethered body's rope untouched
    /// rather than snapping it to a stale or garbage point.</summary>
    public void ResolveTethers() {
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is not { Active: true, Body: { TetherLength: not null } body }) {
                continue;
            }

            FixedVector3 anchor;

            if (body.TetherAnchorBodyIndex is { } anchorIndex) {
                if (
                    (anchorIndex >= Capacity) ||
                    (m_entries[anchorIndex] is not { Active: true, Body: { } anchorBody })
                ) {
                    continue;
                }

                var anchorOrientation = anchorBody.FixedOrientation;
                var anchorPosition = anchorBody.FixedPosition;
                var localOffset = body.TetherAnchorPointOrLocalOffset;

                anchor = FixedTetherConstraint.ResolveAnchor(
                    anchorOrientation: in anchorOrientation,
                    anchorPosition: in anchorPosition,
                    localOffset: in localOffset
                );
            } else {
                anchor = body.TetherAnchorPointOrLocalOffset;
            }

            body.SolveTether(anchor: anchor);
        }
    }
    /// <summary>Looks up a declared body motion program by name — the same table every kit's <see cref="WorldBody"/>
    /// resolves against, exposed so a caller (the <c>body.motion</c> switch door) can validate coherence before
    /// asking a body to switch.</summary>
    /// <param name="name">The declared program name.</param>
    /// <param name="program">The compiled program, or <see langword="null"/> when <paramref name="name"/> is undeclared.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a declared program.</returns>
    public bool TryGetBodyMotionProgram(string name, out CompiledBodyMotionProgram? program) => m_bodyMotionPrograms.TryGetValue(
        key: name,
        value: out program
    );
    /// <summary>Resolves an authored target register by name.</summary>
    public bool TryResolveTargetRegister(string name, out int index) => m_targets.TryGetIndex(
        index: out index,
        name: name
    );
}
