using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Fields;
using Puck.Physics.Motion;
using Puck.Physics.Navigation;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    private static readonly Func<WorldKit, string> KitRowName = static kit => kit.Name;
    private static readonly Func<WorldLook, string> LookRowName = static look => look.Name;

    // Compile the definition's sim-affecting sections to the fixed-point tables runtime simulation reads: the profileless
    // motion tuning, kit producer parameters, kit rows and their fixed compilations, and the resolved seat-kit row. Shared by the
    // constructor and Rebuild so a live retune quantizes through exactly the same path.
    private void CompileFixedTables(WorldDefinition definition, WorldSolidField? solids) {
        var previousNavigation = (m_navigation ?? null);

        LocalSeatCount = definition.Population.LocalSeats;
        var authoredMotion = definition.Motion;

        m_fixedMotion = WorldMotionTuningFactory.Compile(motion: in authoredMotion);
        m_playerDefaults = definition.PlayerDefaults;
        m_peerVariation = definition.Population.PeerVariation;
        m_seatVariation = definition.Population.SeatVariation;
        m_peerColors = definition.Population.PeerColors;
        m_reconnectGraceTicks = definition.PopulationReconnectGraceTicks;
        // The validator admits only a floor that lands on a whole engine tick.
        _ = FixedTickConversion.TryDurationEngineTicksExact(
            seconds: definition.Population.SleepAfterSeconds,
            ticks: out m_sleepAfterTicks
        );
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
        InputCatalog = WorldInputCatalog.For(definition: definition);
        m_channels = InputCatalog.DefaultMapping.Channels;
        m_bodyUpPolicy = WorldBodyUpPolicyCompiler.Compile(collision: definition.Collision);
        m_walkableThreshold = FixedWorldCollision.Compile(collision: definition.Collision).GroundedThreshold;
        m_bodyContactPolicy = definition.Collision.BodyContacts;
        m_rigidContactPolicy = RigidContactPolicy.FromAuthored(policy: m_bodyContactPolicy);
        m_rigidVelocityCeiling = WorldFacePortalPolicy.SpeedCeiling(definition: definition);
        m_targetRows = definition.TargetRegisters;
        m_targets = WorldTargetRegisterTable.Compile(
            registers: definition.TargetRegisters
        );
        // The LIVE row list — a curve-follow producer's per-tick Evaluate reads m_curveRows[CurveIndex].Compiled
        // straight off this reference, so a curve upsert (which reassigns definition.Curves and re-runs Rebuild)
        // retargets every follower on delivery without a second per-tick indirection.
        m_curveRows = definition.Curves;
        m_curves = WorldCurveTable.Compile(curves: definition.Curves);
        m_navigationTable = WorldNavigationDomainTable.Compile(section: definition.Navigation);
        m_inputKits = new FixedWorldKit[InputCatalog.Mappings.Length][];
        var mappedKits = new Dictionary<WorldChannelTable, FixedWorldKit[]>();

        foreach (var mapping in InputCatalog.Mappings) {
            if (mappedKits.TryGetValue(key: mapping.Channels, value: out var existing)) {
                m_inputKits[mapping.Id] = existing;
                continue;
            }
            var kits = new FixedWorldKit[definition.Kits.Count];

            for (var kit = 0; (kit < kits.Length); kit++) {
                kits[kit] = FixedWorldKit.Compile(
                kit: definition.Kits[kit],
                channels: mapping.Channels,
                targets: m_targets,
                curves: m_curves,
                navigation: m_navigationTable,
                programs: m_bodyMotionPrograms,
                programRows: programRows,
                creations: definition.Creations,
                bodyState: definition.BodyState,
                identityState: definition.IdentityState,
                dynamics: definition.Dynamics,
                simulationRateHz: definition.SimulationRateHz
            );
            }
            mappedKits.Add(key: mapping.Channels, value: kits);
            m_inputKits[mapping.Id] = kits;
        }
        m_kits = m_inputKits[InputCatalog.DefaultMapping.Id];


        CompileFlocks();

        // The gravitational field the definition authors. Compiled here beside the contact field so one derivation
        // serves a fresh activation and a live body alike.
        m_gravityField = new WorldGravityField(
            capacity: Capacity,
            compiled: FixedWorldGravity.Compile(definition: definition)
        );

        // The field lattices (InstallFields) were installed before this build; the solid consumers read them.
        CompileSolidConsumers(
            definition: definition,
            previousNavigation: previousNavigation,
            solids: solids
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
        // The remote-human admission cap moves with the live document (a swap can raise or lower networkPlayers).
        m_remoteCap = definition.Population.NetworkPlayers;
    }
    // What the solid rows compile into: the contact field the definition selects (the ONE derivation both a fresh
    // activation and a live body read; the field provider's program is handed in pre-built at runtime, and at boot it
    // is compiled here), the line-of-sight field and the navigation runtime over them. A whole rebuild and a response
    // swap that moves a solid row (TryInstallResponseSolids) both compile them through here.
    private void CompileSolidConsumers(WorldDefinition definition, WorldSolidField? solids, NavigationRuntime? previousNavigation, WorldDefinition? previousDefinition = null) {
        var previousMoving = MovingSolids;

        m_contactCensus = null;
        m_censusDefinition = definition;
        var tuning = FixedWorldCollision.Compile(collision: definition.Collision);
        var derivedSolids = solids?.WithRegions(regions: m_fieldRegions, tuning: tuning);

        m_contactReach = tuning.ContactReach;

        if (
            (derivedSolids is null) &&
            WorldDocument.RequiresSolidField(definition: definition)
        ) {
            if (!WorldSolidField.TryBuild(
                built: out derivedSolids,
                definition: definition,
                reason: out var reason,
                regions: m_fieldRegions
            )) {
                throw new InvalidOperationException(message: $"the target/contact field could not compile the world's solids at boot: {reason}");
            }
        }
        m_baseContactField = ResolveContactField(
            definition: definition,
            solids: derivedSolids,
            previousDefinition: previousDefinition
        );
        MovingSolids?.CarrySamplesFrom(previous: previousMoving);
        if ((derivedSolids is not null) && (m_baseContactField is WorldColliderSet analytic)) {
            derivedSolids = derivedSolids.WithRegions(movingSolids: analytic, regions: m_fieldRegions, tuning: tuning);
        }
        m_adjacencyDefinition = definition;
        ComposeContactField();
        m_targetField = (WorldTargetSelection.RequiresLineOfSight(definition: definition)
            ? derivedSolids
            : null
        );
        var areas = WorldNavigationAreas.Compile(definition: definition);

        m_navigationAreas = areas;
        m_navigation = new NavigationRuntime(
            areas: areas.Set,
            domains: CompileNavigationDomains(areas: areas, definition: definition),
            geometry: NavigationGeometry(contact: m_baseContactField),
            fields: ((m_fieldRegions.Length == 0) ? null : new NavigationMediumFieldAdapter(regions: m_fieldRegions)),
            capacity: new NavigationCapacity(
                GoalFieldBytes: WorldNavigationCapacity.MaxGoalFieldBytes,
                MaxMediumSegmentSubdivisions: WorldNavigationCapacity.MaxMediumSegmentSubdivisions
            ),
            previous: previousNavigation
        );
        if (NavigationBakesAhead(definition: definition)) {
            m_navigation.BeginBakes();
        }
    }
    // The static part of the contact provider the bodies collide with, which navigation plans against: the analytic
    // set's static colliders, or the field's authored program, without the attached solids that move each tick (or
    // the lattice's columns), so a bake reads geometry that never changes and may run off the authority's tick.
    private static IContactField? NavigationGeometry(IContactField? contact) => contact switch {
        WorldColliderSet analytic => analytic.StaticField,
        WorldSolidField field => field.StaticField,
        _ => null,
    };
    // Whether a body may follow a route from its first tick: a kit's target or flock names a navigation domain. Its
    // domains' bakes then start on the thread pool at install, so the first tick that reads one rarely waits.
    private static bool NavigationBakesAhead(WorldDefinition definition) =>
        (((definition.Navigation.Domains?.Count ?? 0) != 0) &&
         (definition.BodyMotionPrograms.Any(predicate: static program => (program.Target is BodyTargetSource.Navigated)) ||
          definition.Kits.Any(predicate: static kit => kit.Producers.Values.Any(predicate: static parameters => (parameters?.Flock?.MovementDomain is not null)))));

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

        if (definition.FieldRegions.Count != definition.FieldPrograms.Count) { reason = "physical regions and compiled programs disagree"; return false; }
        if (m_fieldRegions.Length != definition.FieldRegions.Count) { reason = "adding or removing regions changes allocation; restart the host"; return false; }
        for (var index = 0; (index < m_fieldRegions.Length); index++) {
            var region = m_fieldRegions[index]; var document = definition.FieldRegions[index];

            if (region.Name != document.Name) { reason = $"region '{region.Name}' changed identity"; return false; }
            if (!region.Lattice.CanInstallInput(input: CompileFieldLatticeInput(document: document, program: definition.FieldPrograms[index]), reason: out reason)) { return false; }
        }
        reason = null; return true;
    }
    /// <summary>Installs compatible programs preserving independent live cells.</summary>
    /// <remarks>The region array is allocated once, by the first install, and every later install writes its regions in
    /// place, so a consumer compiled over it (the solid field's lattice columns, navigation's media) reads the installed
    /// regions whether or not the population rebuilt.</remarks>
    /// <param name="definition">The definition whose field programs install.</param>
    /// <exception cref="InvalidOperationException">The candidate's field runtime is incompatible with the live one
    /// (<see cref="CanInstallFields"/>).</exception>
    public void InstallFields(WorldDefinition definition) {
        if (!CanInstallFields(definition: definition, reason: out var reason)) { throw new InvalidOperationException(message: reason); }
        if (!m_fieldsCompiled) {
            m_fieldRegions = definition.FieldRegions.Select(selector: (document, index) => new WorldFieldRegion(document.Name,
                new FieldLattice(input: CompileFieldLatticeInput(document: document, program: definition.FieldPrograms[index]), worldSeed: (definition.Generation?.WorldSeed ?? 0UL)), document.TravelResistance)).ToArray();
            m_fieldsCompiled = true;
        } else {
            for (var index = 0; (index < m_fieldRegions.Length); index++) {
                var region = m_fieldRegions[index]; var document = definition.FieldRegions[index];

                region.Lattice.InstallInput(input: CompileFieldLatticeInput(document: document, program: definition.FieldPrograms[index]));
                m_fieldRegions[index] = region with { TravelResistance = document.TravelResistance };
            }
        }
    }
    /// <summary>Flattens a compiled field program into the plain input the Physics kernel reads — the same seam
    /// <c>CompileNavigationDomains</c> crosses for the navigation kernel: the kernel holds no Schema type, so every
    /// authoring-side handle and enum is mapped by hand here rather than trusted to a numeric cast. Public so a
    /// caller (a test fixture, a tool) can build a <see cref="FieldLattice"/> directly from a document without a
    /// live <see cref="WorldPopulation"/>.</summary>
    /// <param name="document">The lattice composite.</param>
    /// <param name="program">The document's own compiled reaction program.</param>
    public static FieldLatticeInput CompileFieldLatticeInput(WorldFieldsSection document, WorldFieldProgram program) {
        var fields = new FieldDescriptorInput[program.Fields.Count];

        for (var index = 0; (index < fields.Length); index++) {
            var descriptor = program.Fields[index];

            fields[index] = new FieldDescriptorInput(
                Name: descriptor.Name,
                Initial: descriptor.Initial,
                Minimum: descriptor.Minimum,
                Maximum: descriptor.Maximum,
                HeightScale: descriptor.HeightScale,
                IsMedium: descriptor.IsMedium,
                Color: document.Fields[index].Color,
                SimulationOnly: document.Fields[index].SimulationOnly
            );
        }

        var fieldsByName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < fields.Length); index++) {
            fieldsByName[fields[index].Name] = index;
        }

        static FieldScalarInput Scalar(WorldFieldScalarInput input) => new(
            Literal: input.Literal,
            State: input.State
        );

        var reactions = new FieldReactionInput[program.Nodes.Count];

        for (var index = 0; (index < reactions.Length); index++) {
            reactions[index] = program.Nodes[index] switch {
                WorldFieldNode.Diffuse diffuse => new FieldReactionInput.Diffuse(
                Field: diffuse.Field.Ordinal,
                Rate: Scalar(input: diffuse.Rate)
            ),
                WorldFieldNode.Decay decay => new FieldReactionInput.Decay(
                Field: decay.Field.Ordinal,
                Rate: Scalar(input: decay.Rate)
            ),
                WorldFieldNode.Transform transform => new FieldReactionInput.Transform(
                When: [.. transform.When.Select(selector: condition => new FieldConditionInput(
                        condition.Field.Ordinal,
                        condition.Comparison,
                        Scalar(input: condition.Value)
                    ))],
                Then: [.. transform.Then.Select(selector: write => new FieldWriteInput(
                        TakeFrom: write.TakeFrom?.Ordinal,
                        Retain: ((write.Retain is { } retain) ? Scalar(input: retain) : null),
                        Field: write.Field.Ordinal,
                        Op: MapWriteOp(op: write.Op),
                        Value: Scalar(input: write.Value)
                    ))]
            ),
                WorldFieldNode.Emit emit => new FieldReactionInput.Emit(
                emit.Tag,
                emit.Field.Ordinal,
                Scalar(input: emit.Amount)
            ),
                WorldFieldNode.Expose expose => new FieldReactionInput.Expose(
                expose.Field.Ordinal,
                expose.Comparison,
                Scalar(input: expose.Value),
                expose.Row
            ),
                WorldFieldNode.Flow flow => new FieldReactionInput.Flow(
                flow.Field.Ordinal,
                Scalar(input: flow.Rate),
                [.. flow.Over.Select(selector: static over => over.Ordinal)],
                flow.SpillRow
            ),
                _ => throw new InvalidOperationException(message: "fields.reactions carries an unknown reaction kind."),
            };
        }

        var paint = new List<FieldFillInput>();

        foreach (var fill in (document.Paint ?? [])) {
            if (!fieldsByName.TryGetValue(
                key: fill.Field,
                value: out var field
            )) {
                throw new InvalidOperationException(message: $"fields: '{fill.Field}' is not a declared field.");
            }

            paint.Add(item: fill switch {
                WorldLatticeFill.Rect rect => new FieldFillInput.Rect(
                field,
                FixedQ4816.FromDouble(value: rect.Value),
                FixedQ4816.FromDouble(value: rect.MinX),
                FixedQ4816.FromDouble(value: rect.MinZ),
                FixedQ4816.FromDouble(value: rect.MaxX),
                FixedQ4816.FromDouble(value: rect.MaxZ)
            ),
                WorldLatticeFill.Noise noise => new FieldFillInput.Noise(
                field,
                FixedQ4816.FromDouble(value: noise.Value),
                noise.Frequency,
                FixedQ4816.FromDouble(value: noise.Threshold),
                noise.Octaves,
                noise.Seed
            ),
                WorldLatticeFill.Scatter scatter => new FieldFillInput.Scatter(
                field,
                FixedQ4816.FromDouble(value: scatter.Value),
                scatter.Spacing,
                scatter.Radius,
                scatter.Seed
            ),
                WorldLatticeFill.Draw => new FieldFillInput.DrawMarker(Field: field),
                _ => throw new InvalidOperationException(message: "fields.paint carries an unknown fill kind."),
            });
        }

        return new FieldLatticeInput(
            Lattice: new FieldLatticeTopology(
                Origin: FixedVector3.FromVector3(value: document.Lattice.Origin),
                CellSize: FixedQ4816.FromDouble(value: document.Lattice.CellSize),
                Width: document.Lattice.Width,
                Depth: document.Lattice.Depth,
                Layers: document.Lattice.Layers,
                StepEveryTicks: document.Lattice.StepEveryTicks
            ),
            Fields: fields,
            Reactions: reactions,
            Paint: paint
        );
    }

    private static FieldWriteOp MapWriteOp(WorldFieldWriteOp op) => op switch {
        WorldFieldWriteOp.Set => FieldWriteOp.Set,
        WorldFieldWriteOp.Add => FieldWriteOp.Add,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(op),
        actualValue: op,
        message: null
    ),
    };
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
    // ONE place any of the three changes. A definition authoring no adjacency and no portal face, or no injected
    // source, leaves m_contactField pointing at m_baseContactField directly.
    private void ComposeContactField() {
        m_contactFieldInstallVersion++;

        if (
            (m_baseContactField is not { } baseField) ||
            (m_adjacencyDefinition is not { } definition)
        ) {
            m_contactField = m_baseContactField;

            return;
        }

        var bands = ((m_adjacencyAuthority is not null)
            ? WorldAdjacencyBands.CollectFrom(definition: definition)
            : []
        );

        // A portal's opening ignores this world's geometry behind its glass for a body standing in it.
        var opened = WorldPortalHoleContactField.Wrap(
            closures: PortalClosures,
            definition: definition,
            inner: baseField
        );

        m_contactField = (((m_adjacencyAuthority is { } authority) && (bands.Count > 0))
            ? new WorldAdjacencyContactField(
                inner: opened,
                population: this,
                authority: authority
            )
            : opened
        );
    }
    // The requirements-selected contact field: the analytic convex-collider set when no field quality is required, or
    // the pre-built SDF field otherwise. At runtime the
    // server hands the pre-built field (built once at apply time for its loud excluded-op rejection); at boot (solids ==
    // null) the field is compiled here and a bad-op world fails loudly.
    // An analytic set reuses the colliders of each row that stands from the set it replaces.
    private IContactField? ResolveContactField(WorldDefinition definition, WorldSolidField? solids, WorldDefinition? previousDefinition) {
        if (WorldContactSelection.RequiresField(collision: definition.Collision)) {
            if (solids is not null) {
                return solids;
            }

            return (solids ?? throw new InvalidOperationException(message: "the field contact provider was not compiled."));
        }

        return WorldColliderSet.Build(
            definition: definition,
            previous: (m_baseContactField as WorldColliderSet),
            previousDefinition: previousDefinition
        );
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
    // The kit row a population index actually runs: the row its own selector cell picks (kits.assignment.select), else
    // for a local seat (0..LocalSeatCount) or admitted remote human (IsRemoteHuman) the resolved seat kit (m_seatKit),
    // never its entry's own KitIndex — the seat kit can differ from an entry's assigned crowd row on a multi-kit world
    // — else the entry's KitIndex. Every seat-vs-peer kit read (recompile, producer-support checks, kit-replace safety,
    // and the runtime coherence door) shares this ONE resolver so they can never disagree.
    internal bool UsesSeatKit(int index) => ((index < LocalSeatCount) || m_entries[index].IsRemoteHuman);

    /// <summary>Returns everything a slot's kit resolves from, packed: its selected kit, its assigned kit, the seat
    /// kit when it is a seat or remote human, and whether it is active. Two equal answers under one definition resolve
    /// the same kit, committed (<see cref="ResolveKitIndex"/>) and candidate (<see cref="CandidateKitName"/>, given the
    /// selector register's value).</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The packed answer.</returns>
    internal long KitSignature(int index) {
        var entry = m_entries[index];

        return ((((long)(entry.SelectedKit + 1)) << 32) | (((long)entry.KitIndex) << 16) | (((long)(UsesSeatKit(index: index) ? (m_seatKit + 1) : 0)) << 1) | (entry.Active ? 1L : 0L));
    }
    private byte ResolveKitIndex(int index) => ((m_entries[index].SelectedKit >= 0)
        ? (byte)m_entries[index].SelectedKit
        : (UsesSeatKit(index: index)
            ? m_seatKit
            : m_entries[index].KitIndex
        )
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
    /// <param name="authority">The local identity, or <see langword="null"/> to use local geometry alone.</param>
    internal void ConfigureAdjacencies(string? authority) {
        if (string.Equals(m_adjacencyAuthority, authority, StringComparison.Ordinal)) {
            return;
        }

        m_adjacencyAuthority = authority;
        m_adjacencyImageTick = null;
        _ = HoldAdjacency(null);
        ComposeContactField();

        // The composition root configures the runtime adjacency source after the boot seats already exist. Bodies retain
        // their own field reference, so recomposing only the population's field would leave those live bodies on the
        // old base field forever. Hand the effective field to every live body on the same terms as Rebuild; pose,
        // velocity, intent, and every other body property remain untouched.
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is { Active: true, Body: { } body }) {
                body.SetContactConfiguration(
                    field: m_contactField,
                    upPolicy: m_bodyUpPolicy,
                    walkableThreshold: m_walkableThreshold
                );
                body.SetGravityField(field: m_gravityField);
            }
        }
    }

    // Carries one body's navigation route and producer domain from the runtime a recompile replaced into the current one,
    // by domain name. A route stores domain-local node ordinals, so it is kept only when the replacement runtime proved
    // the same complete static bake and rebound its dynamic query provider; every uncertain case replans from the
    // retained designation on the next producer tick. A body that holds no route keeps its navigation state whole: a
    // recompile it played no part in (a placement response swapping a lift door's collider) changes nothing it carries.
    private void RebindNavigation(int bodyIndex, NavigationRuntime previousNavigation) {
        var navigation = m_entries[bodyIndex].NavigationState;

        if ((((uint)navigation.DomainIndex) < ((uint)previousNavigation.Count)) && TryFindRetainedDomain(
            index: out var currentIndex,
            name: previousNavigation[navigation.DomainIndex].Name
        )) {
            navigation.DomainIndex = currentIndex;
        } else if (navigation.DomainIndex >= 0) {
            navigation.Clear();
        }
        var activeDomain = m_entries[bodyIndex].ProducerState.ActiveProducerNavigationDomainIndex;

        if (
            (((uint)activeDomain) < ((uint)previousNavigation.Count)) &&
            TryFindRetainedDomain(
            previousNavigation[activeDomain].Name,
            out var reboundDomain
        )
        ) {
            m_entries[bodyIndex].ProducerState.ActiveProducerNavigationDomainIndex = reboundDomain;
        } else if (activeDomain >= 0) {
            m_entries[bodyIndex].ProducerState.ActiveProducerNavigationDomainIndex = -1;
        }
    }
    private bool TryFindRetainedDomain(string name, out int index) {
        for (var candidate = 0; (candidate < m_navigation.Count); candidate++) {
            if (
                m_navigation.WasRetained(index: candidate) &&
                string.Equals(
                a: m_navigation[candidate].Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                index = candidate;
                return true;
            }
        }
        index = -1;
        return false;
    }

    /// <summary>Recompiles the population's derived state after a sim-affecting section mutation (a live kit tune, a
    /// motion/producer retune, a seat-kit or assignment change, or a whole-document swap): re-quantizes the fixed tables,
    /// re-resolves every entry's kit index, re-derives the kit/producer-dependent per-entry statics without resetting the
    /// running producer phase, and swaps every live body's compiled tuning/actions/program in place — bodies keep their
    /// pose/velocity/tape, only the compiled feel swaps. Bumps <see cref="Revision"/> so the client rebuilds the avatar
    /// program. New activations re-seed fully from these fresh tables.</summary>
    /// <param name="definition">The new live definition, whose field lattices were installed first
    /// (<see cref="InstallFields"/>).</param>
    /// <param name="solids">The server's pre-built SDF contact field for the field provider (built once at apply time so
    /// a runtime edit never rebuilds it twice), or <see langword="null"/> under the analytic provider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void Rebuild(WorldDefinition definition, WorldSolidField? solids) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var recording = WorldDocumentReads.Begin();

        try {
            RebuildCore(definition: definition, solids: solids);
        } finally {
            BuiltFrom = recording.End();
        }
    }
    private void RebuildCore(WorldDefinition definition, WorldSolidField? solids) {
        var previousNavigation = m_navigation;

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
            RebindNavigation(
                bodyIndex: bodyIndex,
                previousNavigation: previousNavigation
            );
            // Cadence periods and cached steering are compiled-kit products. A live kit/assignment rebuild must not
            // carry a partial interval or producer image authored under the previous row into the replacement.
            m_entries[bodyIndex].AutonomyState.Clear();
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

        // Re-resolve the look table too — a live look row or assignment edit rebuilds the population, whose bumped
        // revision rebuilds the client program. PRESENTATION-ONLY, so it touches no body state.
        ResolveLookIndices(definition: definition);
        // The selections index the fresh kit and look tables, so they resolve before anything reads a body's kit.
        _ = ResolveSelections(definition: definition);

        // Re-derive the kit/producer-dependent per-entry statics from the fresh tables, but keep the running producer phase
        // (resetPhase: false) so the live crowd's producer stays continuous — no phase jerk on a retune.
        for (var index = LocalSeatCount; (index < Capacity); index++) {
            SeedSimulated(
                index: index,
                resetPhase: false
            );
        }

        for (var slot = 0; (slot < Capacity); slot++) {
            if (m_entries[slot].Active && UsesSeatKit(index: slot)) {
                SeedSeatSteeringProducer(
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

            RecompileBodyKit(
                body: body,
                kitIndex: ResolveKitIndex(index: index)
            );
            // Hand the (possibly rebuilt) contact field to every live body, so a live solid-geometry or collision-tuning
            // edit takes effect on the next tick.
            body.SetContactConfiguration(
                field: m_contactField,
                upPolicy: m_bodyUpPolicy,
                walkableThreshold: m_walkableThreshold
            );
            body.SetGravityField(field: m_gravityField);
        }

        m_revision++;
    }
    /// <summary>
    /// Resolves active local body pairs after every body has advanced, then solves the tick's rigid step. One broadphase
    /// and one narrowphase query per pair run over the awake solid bodies and the sleepers near them, in deterministic
    /// sweep order with population index as the complete tie-breaker. Two kit bodies share their overlap equally
    /// unless a side's sweep was refused; a pair with a rigid side wakes its sleepers and becomes that step's body
    /// contact, solved with every rigid body's surface contacts by one <see cref="FixedRigidWorld"/> step
    /// (WorldPopulation.Rigid.cs). The complete resting islands then park.
    /// </summary>
    public void ResolveDynamicContacts() {
        var contacts = m_dynamicContactSpheres;
        var count = 0;
        var included = m_dynamicContactIncluded;

        Array.Clear(array: included);

        foreach (var index in m_awakeBodies) {
            if (
                !m_entries[index].Active ||
                (BodyContact(index: index) != WorldBodyContactMode.Solid) ||
                (m_entries[index].Body is not { Collider: not null, MountedOn: null } body) ||
                (!body.OrdinaryAdvanceAdmitted && (body.CarriedBy is null))
            ) {
                continue;
            }

            // A body reaches as far as it can move this step, plus half the rest band a pair keeps its contact within,
            // so a rigid pair is found before it overlaps.
            var travel = body.ContactTravel(stepTicks: m_rigidTickStepTicks);

            m_dynamicContactTravel[index] = travel;
            contacts[count++] = new FixedSweepSphere(
                Index: index,
                Center: body.FixedPosition,
                Radius: ((body.BroadphaseRadius + travel) + RigidRestOffset),
                Active: !body.PopulationSleeping
            );
            included[index] = true;
        }

        DynamicContactCandidates = 0;
        DynamicContactLimitedBodies = 0;
        DynamicContactNarrowPairs = 0;
        DynamicContactResolvedPairs = 0;
        m_rigidPairHits.Clear();

        // Frozen bodies retain their spatial index. Only bodies near an awake contact enter this sweep, found with the
        // awake body's travel-grown sweep radius plus the rest offset a sleeper's sweep radius adds to its own: exactly
        // the bound the pair test below applies, so no pair it would form is missed.
        for (var ordinal = 0; (ordinal < count); ordinal++) {
            var body = m_entries[contacts[ordinal].Index].Body!;

            if (body.PopulationSleeping) { continue; }
            var nearby = NearbySleepingBodies(body: body, destination: m_sleepQueries, reach: (contacts[ordinal].Radius + RigidRestOffset));

            for (var neighbour = 0; (neighbour < nearby); neighbour++) {
                var index = m_sleepQueries[neighbour];

                if (included[index] || (BodyContact(index: index) != WorldBodyContactMode.Solid)) { continue; }
                var sleeper = m_entries[index].Body!;

                included[index] = true;
                m_dynamicContactTravel[index] = FixedQ4816.Zero;
                contacts[count++] = new FixedSweepSphere(
                    Index: index,
                    Center: sleeper.FixedPosition,
                    Radius: (sleeper.BroadphaseRadius + RigidRestOffset),
                    Active: !sleeper.PopulationSleeping
                );
            }
        }

        // The one broadphase-plus-narrowphase sweep over the work set. A frozen left body's candidates are awake bodies
        // only, so frozen/frozen pairs are never visited or charged against a contact budget.
        var grid = m_dynamicContactGrid;

        grid.Rebuild(spheres: contacts.AsSpan(length: count, start: 0));
        Array.Clear(array: m_dynamicContactDegrees);
        for (var leftOrdinal = 0; (leftOrdinal < count); leftOrdinal++) {
            var leftIndex = grid.Sphere(ordinal: leftOrdinal).Index;
            var left = m_entries[leftIndex].Body!;

            if (m_dynamicContactDegrees[leftIndex] >= m_bodyContactPolicy.MaxPairsPerBody) {
                continue;
            }
            // Read live: a body an earlier pair woke searches the whole image.
            grid.BeginPairs(activeOnly: left.PopulationSleeping, candidateBudget: m_bodyContactPolicy.CandidateBudget, leftOrdinal: leftOrdinal);
            while (grid.NextPair(rightOrdinal: out var rightOrdinal)) {
                DynamicContactCandidates++;
                var rightIndex = grid.Sphere(ordinal: rightOrdinal).Index;
                var right = m_entries[rightIndex].Body!;

                // Held bodies are static solver sides only against a free rigid body. Their carrier is never a
                // contact partner; locomotion neighbours are resolved by the carry block pass.
                if (((left.CarriedBy is { } leftCarrier) &&
                        ((leftCarrier == rightIndex) || !right.IsRigid || (right.CarriedBy is not null))) ||
                    ((right.CarriedBy is { } rightCarrier) &&
                        ((rightCarrier == leftIndex) || !left.IsRigid || (left.CarriedBy is not null)))) {
                    continue;
                }

                if (m_dynamicContactDegrees[rightIndex] >= m_bodyContactPolicy.MaxPairsPerBody) {
                    continue;
                }
                DynamicContactNarrowPairs++;

                var pairIsRigid = (left.IsRigid || right.IsRigid);
                // A rigid pair is solved speculatively: within what both can travel this step plus the rest band,
                // its contact exists before it overlaps. Two kit bodies resolve by position, overlap only.
                var margin = (pairIsRigid ? ((m_dynamicContactTravel[leftIndex] + m_dynamicContactTravel[rightIndex]) + RigidRestBand) : FixedQ4816.Zero);

                if (FixedDynamicBodyContacts.TryCorrection(
                    leftPosition: left.FixedPosition,
                    leftOrientation: left.FixedOrientation,
                    leftVolumes: left.ScaledColliderVolumes(),
                    rightPosition: right.FixedPosition,
                    rightOrientation: right.FixedOrientation,
                    rightVolumes: right.ScaledColliderVolumes(),
                    tieBreaker: leftIndex ^ rightIndex,
                    correction: out var correction,
                    margin: margin
                )) {
                    var depth = (correction.Length - margin);

                    if (pairIsRigid && (left.PopulationSleeping || right.PopulationSleeping) &&
                        RestingContactHolds(depth: depth, left: leftIndex, right: rightIndex)) {
                        // The sleeper rests in this very contact, and a contact no deeper than its rest wakes nothing.
                        continue;
                    }
                    if (pairIsRigid) {
                        // The pair is a body contact of this tick's rigid step; its sleepers join the step.
                        if (left.PopulationSleeping) { WakeBodyIsland(index: leftIndex); }
                        if (right.PopulationSleeping) { WakeBodyIsland(index: rightIndex); }
                        m_rigidPairHits.Add(item: new RigidPairHit(Correction: correction, Depth: depth, Left: leftIndex, Right: rightIndex));
                    } else {
                        var (leftShare, rightShare) = SplitPairCorrection(
                            correction: correction,
                            left: left,
                            right: right
                        );

                        left.ApplyDynamicContact(correction: leftShare);
                        right.ApplyDynamicContact(correction: rightShare);
                    }
                    m_dynamicContactDegrees[leftIndex]++;
                    m_dynamicContactDegrees[rightIndex]++;
                    DynamicContactResolvedPairs++;
                    if (m_dynamicContactDegrees[leftIndex] >= m_bodyContactPolicy.MaxPairsPerBody) {
                        break;
                    }
                }
            }
            if (grid.PairsLimited) {
                DynamicContactLimitedBodies++;
            }
        }

        DynamicContactPairVisits = grid.PairVisits;
        DynamicContactPotentialPairs = ((count * (count - 1)) / 2);
        SolveRigidContacts();
        ParkRigidIslands();
    }

    /// <summary>Splits a body pair's overlap correction (added to <paramref name="left"/>, subtracted from
    /// <paramref name="right"/>) between the two: half each, unless a side's sweep was refused this tick. A refused
    /// body is immovable for the rest of its tick, so the other side is resolved against it as static and takes the
    /// whole correction; a pair of refused bodies moves neither.</summary>
    /// <returns>The correction to add to each side's position.</returns>
    private static (FixedVector3 Left, FixedVector3 Right) SplitPairCorrection(WorldBody left, WorldBody right, FixedVector3 correction) {
        if (left.SweepRefusedThisTick) {
            return (FixedVector3.Zero, (right.SweepRefusedThisTick ? FixedVector3.Zero : -correction));
        }

        if (right.SweepRefusedThisTick) {
            return (correction, FixedVector3.Zero);
        }

        var shared = (correction / FixedQ4816.FromInteger(value: 2L));

        return (shared, -shared);
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
