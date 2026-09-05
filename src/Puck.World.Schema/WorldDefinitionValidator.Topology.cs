using Puck.Abstractions.Presentation;
using Puck.Maths;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The host section (PRESENTATION-ONLY): window extents bounded, exit/pacing non-negative, the closed engine enums
    // in range (a mutation can carry an out-of-range cast the JSON converter alone would not catch), and the surface
    // format not the Unknown hole. Genlock is SHAPE-only (null or non-whitespace) — unlike storage.endpoint (nothing yet
    // consumes it), genlock IS wired at boot into the external-clock election, which tolerates an unknown source id.
    /// <summary>Refuses, at authoring, any token the backend site could emit that names no backend. Left to the
    /// settle-time parse alone this would be a coin-flip refusal: a weighted table carrying one bad token boots fine
    /// on every seed that does not draw it, so whether the world starts would move with the world seed and the
    /// instance identity. Every reachable token is checked instead — the same reason a numeric site's distribution is
    /// narrowed against its domain rather than against what it happened to roll.</summary>
    private static void ValidateBackendTokens(WorldDraw draw, IReadOnlyList<WorldGeneratorRow>? generators, List<string> errors) {
        if (!WorldGeneratorEngine.TryResolveSource(
            draw: draw,
            generator: out var generator,
            generators: generators,
            reason: out _
        )) {
            return;
        }

        foreach (var context in (generator.Contexts ?? [])) {
            foreach (var alternative in ((context?.Alternatives) ?? [])) {
                if (
                    (alternative is not null) &&
                    (WorldHostTokens.ParseBackend(token: alternative.Token) is null)
                ) {
                    errors.Add(item: $"a backend-row generator could emit token '{alternative.Token}', which names no backend ('{WorldHostTokens.BackendAuto}', '{WorldHostTokens.BackendDirectX}', or '{WorldHostTokens.BackendVulkan}').");
                }
            }
        }
    }
    private static void ValidateDefaultPeerSource(WorldDefinition definition, List<string> errors) {
        var source = definition.Population.DefaultPeerSource;

        if (
            source.IsLive ||
            source.IsIdle
        ) {
            return;
        }
        if (
            !source.IsProducer ||
            (source.ProducerName is not { } producerName)
        ) {
            errors.Add(item: $"bodies.defaultPeerSource '{source}' is not a defined IntentSource.");

            return;
        }

        IEnumerable<WorldKit> assignedKits = definition.Kits;

        if (definition.Assignment.Rows.Count > 0) {
            assignedKits = definition.Assignment.Rows
                .Select(selector: static name => name.Value)
                .Distinct(comparer: StringComparer.Ordinal)
                .Select(selector: name => WorldDefinitionRows.FindKit(
                kits: definition.Kits,
                name: name
            ))
                .OfType<WorldKit>();
        }

        foreach (var kit in assignedKits) {
            if (
                (kit.Producers is null) ||
                !kit.Producers.ContainsKey(key: producerName)
            ) {
                errors.Add(item: $"bodies.defaultPeerSource names producer '{producerName}', but assigned kit '{kit.Name}' declares no parameters for it.");
            }
        }
    }
    // The destinations section: null names nothing. Each row's Name already crossed WorldSafeName; Durability/Scope
    // already crossed their strict-token converters; an unrecognized Selector $type already failed JSON parse. This
    // pass owns uniqueness within the section, a destinations section with no references section to name, each
    // row's Reference resolving to a declared references row, and the scope/selector pairing (ValidateGroupSelector).
    // Returns the validated name set so a later pass can refuse an undeclared destination by name.
    private static HashSet<string> ValidateDestinations(IReadOnlyList<WorldDestination>? destinations, IReadOnlyList<WorldReference>? references, HashSet<string> referenceNames, HashSet<string> groupIds, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (destinations is not { Count: > 0 } rows) {
            return names;
        }

        if (references is not { Count: > 0 }) {
            errors.Add(item: "destinations declares rows, but the world declares no references section for them to name.");
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"destinations[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!names.Add(item: row.Name)) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated.");
            }

            RequireDeclaredListing(
                declaredSet: referenceNames,
                errors: errors,
                rowNoun: "references row",
                subject: $"{path}.reference '{row.Reference}'",
                value: row.Reference
            );

            if (row.Scope == WorldDestinationScope.Group) {
                if (row.Selector is null) {
                    errors.Add(item: $"{path}.scope is '{WorldDestinationTokens.ScopeGroup}', which requires a selector.");
                } else {
                    ValidateGroupSelector(
                        selector: row.Selector,
                        groupIds: groupIds,
                        path: $"{path}.selector",
                        errors: errors
                    );
                }
            } else if (row.Selector is not null) {
                errors.Add(item: $"{path}.selector is admitted only when scope is '{WorldDestinationTokens.ScopeGroup}' (this row declares scope '{WorldDestinationTokens.ScopeToken(scope: row.Scope)}').");
            }
        }

        return names;
    }
    private static void ValidateHost(WorldHostDefaults host, IReadOnlyList<WorldGeneratorRow>? generators, IReadOnlyList<WorldStateRow> stateRows, List<string> errors) {
        if (!Enum.IsDefined(value: host.Presentation)) {
            errors.Add(item: $"host.presentation '{host.Presentation}' is not a defined WorldHostPresentation.");
        }

        RequireIntRange(
            value: host.Width,
            min: 1,
            max: 16384,
            name: "host.width",
            errors: errors
        );
        RequireIntRange(
            value: host.Height,
            min: 1,
            max: 16384,
            name: "host.height",
            errors: errors
        );
        RequireIntRange(
            value: host.ExitAfterSeconds,
            min: 0,
            max: int.MaxValue,
            name: "host.exitAfterSeconds",
            errors: errors
        );

        if (
            !double.IsFinite(d: host.TargetHertz) ||
            (host.TargetHertz < 0.0)
        ) {
            errors.Add(item: $"host.targetHertz {host.TargetHertz} must be finite and non-negative (0 = automatic display pacing).");
        }

        if (
            (host.Backend is { } backend) &&
            !Enum.IsDefined(value: backend)
        ) {
            errors.Add(item: $"host.backend '{backend}' is not a defined WorldBackendPreference.");
        }

        // The honest XOR this site can afford: WorldHostDefaults is a CLASS, so a null Backend is distinguishable
        // from an authored one and declaring both is refused BY NAME (bodies.capacityDraw's struct-typed site
        // cannot do this — see its own remarks). Declaring NEITHER stays legitimate and reads as 'auto'.
        if (
            (host.Backend is not null) &&
            (host.BackendRow is not null)
        ) {
            errors.Add(item: "host declares both 'backend' and 'backendRow' — the backend is an authored literal or a row read, never both.");
        }

        if (host.BackendRow is { } backendRow) {
            if (WorldDefinitionRows.FindStateRow(
                name: backendRow,
                rows: stateRows
            ) is not { } tokenRow) {
                errors.Add(item: $"host.backendRow names state row '{backendRow}', which the document does not declare.");
            } else if (
                (tokenRow.Kind != CellKind.Text) ||
                tokenRow.IsKeyed ||
                (tokenRow.Field is not null)
            ) {
                errors.Add(item: $"host.backendRow names state row '{backendRow}', which must be a scalar kind=text row.");
            }
        }

        if (!Enum.IsDefined(value: host.PresentMode)) {
            errors.Add(item: $"host.presentMode '{host.PresentMode}' is not a defined PresentMode.");
        }

        if (
            !Enum.IsDefined(value: host.SurfaceFormat) ||
            (host.SurfaceFormat == SurfaceFormat.Unknown)
        ) {
            errors.Add(item: $"host.surfaceFormat '{host.SurfaceFormat}' must be a defined non-Unknown SurfaceFormat.");
        }

        if (
            (host.Genlock is { } genlock) &&
            string.IsNullOrWhiteSpace(value: genlock)
        ) {
            errors.Add(item: "host.genlock must be non-whitespace or null.");
        }

        // Listen is SHAPE-only too: null (loopback-only, the default) or a non-whitespace "host:port" pair.
        // Server.WorldPeerHost is what actually parses/binds it; the validator only refuses an obviously malformed
        // value so a typo fails loudly at boot rather than surfacing as a silent "never listening".
        if ((host.Listen is { } listen)) {
            if (string.IsNullOrWhiteSpace(value: listen)) {
                errors.Add(item: "host.listen must be a non-whitespace \"host:port\" pair or null.");
            } else {
                var separator = listen.LastIndexOf(value: ':');

                if (
                    (separator <= 0) ||
                    (separator == (listen.Length - 1)) ||
                    !int.TryParse(
                    s: listen[(separator + 1)..],
                    result: out var port
                ) ||
                    (port <= 0) ||
                    (port > 65535)
                ) {
                    errors.Add(item: $"host.listen '{listen}' must be a \"host:port\" pair with a port 1..65535.");
                }
            }
        }

        ValidateHostEndpoint(
            value: host.Authority,
            path: "host.authority",
            errors: errors
        );
    }
    private static void ValidateHostEndpoint(string? value, string path, List<string> errors) {
        if (value is null) {
            return;
        }

        if (string.IsNullOrWhiteSpace(value: value)) {
            errors.Add(item: $"{path} must be a non-whitespace \"host:port\" pair or null.");

            return;
        }

        var separator = value.LastIndexOf(value: ':');

        if (
            (separator <= 0) ||
            (separator == (value.Length - 1)) ||
            !int.TryParse(
            s: value[(separator + 1)..],
            result: out var port
        ) ||
            (port <= 0) ||
            (port > 65535)
        ) {
            errors.Add(item: $"{path} '{value}' must be a \"host:port\" pair with a port 1..65535.");
        }
    }
    /// <summary>Validates the <c>interactions</c> section by compiling it — <see cref="WorldRuleCompiler.CompileAllInteractions"/>
    /// owns which co-occurrence/effect kinds are admissible and which names resolve (the property registry, a region
    /// placement), so this pass calls it and reports its by-name refusal, mirroring <see cref="ValidateRules"/>'s own
    /// division against <see cref="WorldRuleCompiler.CompileAll"/>.</summary>
    private static void ValidateInteractions(WorldInteractionsSection? interactions, WorldDefinition definition, List<string> errors) {
        var rows = (interactions?.Interactions ?? []);

        if (rows.Count == 0) {
            return;
        }

        if (rows.Count > WorldInteractionCapacity.MaxInteractions) {
            errors.Add(item: $"interactions count {rows.Count} exceeds the maximum of {WorldInteractionCapacity.MaxInteractions}.");
        }

        for (var index = 0; (index < rows.Count); index++) {
            var interaction = rows[index];
            var path = $"interactions[{index}]";

            if (interaction is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!Enum.IsDefined(value: interaction.Mode)) {
                errors.Add(item: $"{path}.mode '{interaction.Mode}' is not a defined ActionTriggerMode.");
            }

            if (!Enum.IsDefined(value: interaction.CoOccurrence)) {
                errors.Add(item: $"{path}.coOccurrence '{interaction.CoOccurrence}' is not a defined WorldInteractionCoOccurrence.");
            }

            if (interaction.Effects is not { Count: > 0 }) {
                errors.Add(item: $"{path}.effects must be non-empty — an interaction that does nothing is one nothing can read back.");
            }

            if (
                (interaction.CoOccurrence == WorldInteractionCoOccurrence.Distance) &&
                (interaction.Range < decimal.Zero)
            ) {
                errors.Add(item: $"{path}.range {interaction.Range} is negative — a distance threshold cannot be negative.");
            }
        }

        try {
            _ = WorldRuleCompiler.CompileAllInteractions(definition: definition);
        } catch (WorldRuleException exception) {
            errors.Add(item: exception.Message);
        }
    }
    private static void ValidatePortals(WorldPortalsSection? portals, List<string> errors) {
        if (portals is null) {
            return;
        }

        var defaults = portals.PortalDefaults;

        if (!Enum.IsDefined(value: defaults.Travel)) {
            errors.Add(item: $"portals.portalDefaults.travel '{defaults.Travel}' is not a defined WorldPortalTravel.");
        }

        if (
            !double.IsFinite(d: defaults.HoldSeconds) ||
            (defaults.HoldSeconds <= 0.0)
        ) {
            errors.Add(item: $"portals.portalDefaults.holdSeconds {defaults.HoldSeconds} must be finite and positive.");
        } else if (!FixedTickConversion.TryDurationEngineTicksExact(
            seconds: ((decimal)defaults.HoldSeconds),
            ticks: out _
        )) {
            errors.Add(item: $"portals.portalDefaults.holdSeconds {defaults.HoldSeconds} does not convert to an exact whole tick across the {FixedTickConversion.TicksPerSecond} engine-tick bridge.");
        }

        if (!Enum.IsDefined(value: defaults.Full)) {
            errors.Add(item: $"portals.portalDefaults.full '{defaults.Full}' is not a defined WorldTransferFullPolicy.");
        }
    }
    /// <summary>Validates the <c>properties</c> section — the group-kind-name validated-vocabulary pattern
    /// (<see cref="ValidateGroups"/>) applied to a carrier property name: unique, a legitimate identifier, and backed
    /// by a declared keyed <c>int</c> <c>state</c> row of the same name (see
    /// <see cref="WorldPropertyRegistrySection"/>'s remarks for why storage rides the state substrate rather than a
    /// second one).</summary>
    private static void ValidateProperties(WorldPropertyRegistrySection? properties, Dictionary<string, WorldStateRow> stateRows, List<string> errors) {
        if (properties is null) {
            return;
        }

        if (properties.Names.Count > WorldPropertyCapacity.MaxProperties) {
            errors.Add(item: $"properties.names count {properties.Names.Count} exceeds the maximum of {WorldPropertyCapacity.MaxProperties}.");
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < properties.Names.Count); index++) {
            var name = properties.Names[index];
            var path = $"properties.names[{index}]";

            if (string.IsNullOrWhiteSpace(value: name)) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!WorldCellName.TryParse(
                candidate: name,
                name: out _,
                reason: out var nameReason
            )) {
                errors.Add(item: $"{path} '{name}' {nameReason}.");

                continue;
            }

            if (!seen.Add(item: name)) {
                errors.Add(item: $"{path} '{name}' is duplicated.");

                continue;
            }

            if (!stateRows.TryGetValue(
                key: name,
                value: out var row
            )) {
                errors.Add(item: $"{path} '{name}' names no declared state row — a property's per-carrier tags are stored in a keyed int state row of the SAME name; declare it first with world.row.set state.");
            } else if (row.Kind != CellKind.Int) {
                errors.Add(item: $"{path} '{name}' names state row '{name}', which is kind={row.Kind.ToString().ToLowerInvariant()} — a property's per-carrier tags are stored as kind=int.");
            } else if (!row.IsKeyed) {
                errors.Add(item: $"{path} '{name}' names state row '{name}', which is not keyed — a property's per-carrier tags are one cell per carrier (a keyed row, exactly like an argmax-eligible tally); author it with a 'capacity' (or several cells) so it is keyed.");
            }
        }
    }
    private static HashSet<string> ValidateReferences(IReadOnlyList<WorldReference>? references, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (references is not { Count: > 0 } rows) {
            return names;
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"references[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!names.Add(item: row.Name)) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated.");
            }

            var hasDocument = !string.IsNullOrWhiteSpace(value: row.Document);
            var hasOwner = (row.Owner is not null);
            var hasWorld = (row.World is not null);

            if (
                hasDocument &&
                (hasOwner || hasWorld)
            ) {
                errors.Add(item: $"{path} names both a document and an owner-named world; author exactly one.");
            } else if (hasOwner != hasWorld) {
                errors.Add(item: $"{path} names only one of owner/world; both are required together.");
            } else if (
                !hasDocument &&
                !hasOwner
            ) {
                errors.Add(item: $"{path} names neither a document nor an owner-named world; one is required.");
            }

            if (
                hasDocument &&
                row.Document!.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: OwnerNeighbourKeyPrefix
            )
            ) {
                errors.Add(item: $"{path}.document '{row.Document}' begins with the reserved '{OwnerNeighbourKeyPrefix}' prefix — that spelling is reserved for the owner-named arm.");
            }
        }

        return names;
    }
    /// <summary>Validates the <c>rules</c> section by compiling it — <see cref="WorldRuleCompiler"/> owns which
    /// predicate/effect kinds are admissible at world scope and which names resolve, so this pass calls it and
    /// reports its by-name refusal rather than restating the rule set (the exact division
    /// <c>BodyMotionProgramException</c> already has for kit programs).</summary>
    private static void ValidateRules(IReadOnlyList<WorldRule>? rules, WorldDefinition definition, List<string> errors) {
        if (rules is not { Count: > 0 }) {
            return;
        }

        if (rules.Count > WorldRuleCapacity.MaxRules) {
            errors.Add(item: $"rules count {rules.Count} exceeds the maximum of {WorldRuleCapacity.MaxRules}.");
        }

        for (var index = 0; (index < rules.Count); index++) {
            var rule = rules[index];
            var path = $"rules[{index}]";

            if (rule is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!Enum.IsDefined(value: rule.Mode)) {
                errors.Add(item: $"{path}.mode '{rule.Mode}' is not a defined ActionTriggerMode.");
            }

            if (rule.Effects is null || (rule.Effects.Count == 0 && rule.Decision is null)) {
                errors.Add(item: $"{path}.effects must be non-empty — a rule that does nothing is a rule nothing can read back.");
            }
        }

        try {
            _ = WorldRuleCompiler.CompileAll(definition: definition);
        } catch (WorldRuleException exception) {
            errors.Add(item: exception.Message);
        }
    }
    /// <summary>Gets whether the document declares at least one medium lattice field — the premise a kit authoring
    /// a <c>Medium</c> hold row requires.</summary>
    private static bool HasMediumField(WorldDefinition definition) {
        var fields = (definition.Fields?.Fields ?? []);

        for (var index = 0; (index < fields.Count); index++) {
            if (fields[index].Medium) {
                return true;
            }
        }

        return false;
    }
    private static void ValidateFields(WorldDefinition definition, List<string> errors) {
        ValidateDiscreteState(definition, errors);
        var physical = WorldTopologyCompilation.FindPhysical(definition.StateRaw);
        var physicalCount = (definition.StateRaw?.Lattices ?? []).Count(t => t?.Kind == WorldTopologyKind.Field);
        if (physicalCount > 1) {
            errors.Add("state.lattices admits at most one physical field topology.");
        }
        foreach (var row in definition.StateRaw?.World ?? []) {
            if (row?.Field is not null) {
                var topologyName = (row.EffectiveDomain is WorldStateDomain.CellsOf cellsOf ? cellsOf.Topology : null);

                if (physical is null || topologyName != physical.Name) {
                    errors.Add($"state row '{row.Name}' field domain.topology '{topologyName}' names no physical topology.");
                }
            }
        }
        if (physical is not null && definition.Fields is { Fields.Count: 0 }) {
            errors.Add($"state.lattices '{physical.Name}' is declared but no state row carries a field trait.");
        }
        if (definition.Fields is not { } fields) {
            return;
        }

        static bool FitsFixed(float value) => (
            float.IsFinite(f: value) &&
            (value >= (((double)long.MinValue) / 65536.0)) &&
            (value <= (((double)long.MaxValue) / 65536.0))
        );

        var lattice = fields.Lattice;

        if (lattice is null) {
            errors.Add(item: "fields.lattice is required.");

            return;
        }

        if (
            !float.IsFinite(f: lattice.Origin.X) ||
            !float.IsFinite(f: lattice.Origin.Y) ||
            !float.IsFinite(f: lattice.Origin.Z)
        ) {
            errors.Add(item: "fields.lattice.origin must contain finite coordinates.");
        }

        if (
            !FitsFixed(value: lattice.CellSize) ||
            (FixedQ4816.FromDouble(value: lattice.CellSize) <= FixedQ4816.Zero)
        ) {
            errors.Add(item: $"fields.lattice.cellSize must quantize to a positive Q48.16 value (was {lattice.CellSize}).");
        }

        if (
            !FitsFixed(value: lattice.Origin.X) ||
            !FitsFixed(value: lattice.Origin.Y) ||
            !FitsFixed(value: lattice.Origin.Z)
        ) {
            errors.Add(item: "fields.lattice.origin must fit Q48.16.");
        } else if (
            FitsFixed(value: lattice.CellSize) &&
            (lattice.CellSize > 0f) &&
            (
                !FitsFixed(value: ((float)(((double)lattice.Origin.X) + (((double)lattice.CellSize) * lattice.Width)))) ||
                !FitsFixed(value: ((float)(((double)lattice.Origin.Y) + (((double)lattice.CellSize) * lattice.Layers)))) ||
                !FitsFixed(value: ((float)(((double)lattice.Origin.Z) + (((double)lattice.CellSize) * lattice.Depth))))
            )
        ) {
            errors.Add(item: "fields.lattice extent must fit Q48.16.");
        }

        if (
            (lattice.Width < 1) ||
            (lattice.Width > WorldFieldCapacity.MaxExtent) ||
            (lattice.Depth < 1) ||
            (lattice.Depth > WorldFieldCapacity.MaxExtent)
        ) {
            errors.Add(item: $"fields.lattice.width/depth must be in 1..{WorldFieldCapacity.MaxExtent} (was {lattice.Width}x{lattice.Depth}).");
        }

        if (
            (lattice.Layers < 1) ||
            (lattice.Layers > WorldFieldCapacity.MaxLayers)
        ) {
            errors.Add(item: $"fields.lattice.layers must be in 1..{WorldFieldCapacity.MaxLayers} (was {lattice.Layers}).");
        }

        if (((((long)lattice.Width) * lattice.Depth) * lattice.Layers) > WorldFieldCapacity.MaxCells) {
            errors.Add(item: $"fields.lattice declares {((((long)lattice.Width) * lattice.Depth) * lattice.Layers)} cells, exceeding the {WorldFieldCapacity.MaxCells}-cell ceiling.");
        }

        if (lattice.StepEveryTicks < 1) {
            errors.Add(item: $"fields.lattice.stepEveryTicks must be at least 1 (was {lattice.StepEveryTicks}).");
        }

        var rows = (fields.Fields ?? []);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var hasHeightField = false;

        if (rows.Count == 0) {
            errors.Add(item: "fields.fields declares no field.");
        }

        if (rows.Count > WorldFieldCapacity.MaxFields) {
            errors.Add(item: $"fields.fields declares {rows.Count} rows, exceeding the {WorldFieldCapacity.MaxFields}-field ceiling.");
        }

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"fields.fields[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is null.");

                continue;
            }

            if (
                string.IsNullOrWhiteSpace(value: row.Name) ||
                row.Name.Contains(value: '.') ||
                row.Name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldStateRow.ReservedNamePrefix
            )
            ) {
                errors.Add(item: $"{path}.name '{row.Name}' must be non-empty, dot-free, and not '{WorldStateRow.ReservedNamePrefix}'-prefixed.");
            } else if (!names.Add(item: row.Name)) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated.");
            }

            if (
                !FitsFixed(value: row.Min) ||
                !FitsFixed(value: row.Max) ||
                (FixedQ4816.FromDouble(value: row.Min) >= FixedQ4816.FromDouble(value: row.Max))
            ) {
                errors.Add(item: $"{path} must declare Q48.16-representable min < max after quantization (was {row.Min}..{row.Max}).");
            } else if (
                !FitsFixed(value: row.Initial) ||
                (row.Initial < row.Min) ||
                (row.Initial > row.Max)
            ) {
                errors.Add(item: $"{path}.initial {row.Initial} is outside {row.Min}..{row.Max}.");
            }

            if (
                !FitsFixed(value: row.HeightScale) ||
                (row.HeightScale < 0f)
            ) {
                errors.Add(item: $"{path}.heightScale must be finite and non-negative (was {row.HeightScale}).");
            }

            if (
                row.Medium &&
                (row.HeightScale <= 0f)
            ) {
                errors.Add(item: $"{path}.medium requires a heightScale greater than 0 — a surface-less medium is meaningless.");
            }

            if (row.HeightScale > 0f) {
                hasHeightField = true;

                if (
                    (row.Color is null) ||
                    !WorldColor.IsAuthorable(
                    definition: definition,
                    value: row.Color
                )
                ) {
                    errors.Add(item: $"{path}.color {WorldColor.Grammar} on a field carrying a heightScale.");
                }

                var maximumRaise = ((((double)row.Max) * row.HeightScale) * lattice.Layers);
                var minimumRaise = ((((double)row.Min) * row.HeightScale) * lattice.Layers);

                if (maximumRaise > (WorldFieldCapacity.MaxSurfaceCells * ((double)lattice.CellSize))) {
                    errors.Add(item: $"{path} can raise {maximumRaise} units of surface across {lattice.Layers} layers, above the {(WorldFieldCapacity.MaxSurfaceCells * lattice.CellSize)}-unit ceiling ({WorldFieldCapacity.MaxSurfaceCells} cells of cellSize).");
                }

                if (minimumRaise < (((double)long.MinValue) / 65536.0)) {
                    errors.Add(item: $"{path}'s minimum layered height {minimumRaise} does not fit Q48.16.");
                }
            } else if (
                (row.Color is not null) &&
                !IsHexColor(value: row.Color)
            ) {
                errors.Add(item: $"{path}.color '{row.Color}' is not #RRGGBB.");
            }
        }

        if (
            hasHeightField &&
            ((lattice.Width > WorldFieldCapacity.MaxSurfaceCells) || (lattice.Depth > WorldFieldCapacity.MaxSurfaceCells))
        ) {
            errors.Add(item: $"fields.lattice width/depth must be at most {WorldFieldCapacity.MaxSurfaceCells} when a field carries heightScale; one {(WorldFieldCapacity.MaxSurfaceCells + 2)}-voxel render brick must cover the lattice plus its border (was {lattice.Width}x{lattice.Depth}).");
        }

        var reactions = (fields.Reactions ?? []);

        if (reactions.Count > WorldFieldCapacity.MaxReactions) {
            errors.Add(item: $"fields.reactions declares {reactions.Count} rows, exceeding the {WorldFieldCapacity.MaxReactions}-reaction ceiling.");
        }

        void RequireField(string? name, string path) {
            if (
                (name is null) ||
                !names.Contains(item: name)
            ) {
                errors.Add(item: $"{path} names field '{name}', which fields.fields does not declare.");
            }
        }

        void RequireScalarRow(string row, string path) {
            if (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: row
            ) is not { } declared) {
                errors.Add(item: $"{path} references state row '{row}', which the document does not declare.");
            } else if (
                (declared.Kind != CellKind.Fixed) ||
                declared.IsKeyed ||
                (declared.Field is not null)
            ) {
                errors.Add(item: $"{path} references state row '{row}', which must be a scalar kind=fixed row (a reaction scalar reads one slot cell per step).");
            }
        }

        void RequireRate(WorldLatticeScalar rate, string path) {
            if (rate.Row is { } row) {
                RequireScalarRow(
                    path: path,
                    row: row
                );

                return;
            }

            RequireUnitInterval(
                value: (rate.Literal ?? 0f),
                name: path,
                errors: errors
            );
        }

        void RequireScalarValue(WorldLatticeScalar value, string path) {
            if (value.Row is { } row) {
                RequireScalarRow(
                    path: path,
                    row: row
                );

                return;
            }

            if (!FitsFixed(value: (value.Literal ?? 0f))) {
                errors.Add(item: $"{path} must carry a finite Q48.16 value.");
            }
        }

        void RequireKeyedIntRow(string? row, string path) {
            if (
                (row is null) ||
                (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: row
            ) is not { } declared)
            ) {
                errors.Add(item: $"{path} names state row '{row}', which the document does not declare.");
            } else if (
                (declared.Kind != CellKind.Int) ||
                !declared.IsKeyed
            ) {
                errors.Add(item: $"{path} names state row '{row}', which must be a keyed kind=int row.");
            }
        }

        for (var index = 0; (index < reactions.Count); index++) {
            var path = $"fields.reactions[{index}]";

            switch (reactions[index]) {
                case WorldReaction.Diffuse diffuse:
                    RequireField(
                        name: diffuse.Field,
                        path: $"{path}.field"
                    );
                    RequireRate(
                        rate: diffuse.Rate,
                        path: $"{path}.rate"
                    );
                    break;
                case WorldReaction.Decay decay:
                    RequireField(
                        name: decay.Field,
                        path: $"{path}.field"
                    );
                    RequireRate(
                        rate: decay.Rate,
                        path: $"{path}.rate"
                    );
                    break;
                case WorldReaction.Transform transform: {
                        var conditions = (transform.When ?? []);
                        var writes = (transform.Then ?? []);

                        if (conditions.Count > WorldFieldCapacity.MaxTransformTerms) {
                            errors.Add(item: $"{path}.when declares {conditions.Count} conditions, exceeding the {WorldFieldCapacity.MaxTransformTerms}-term ceiling.");
                        }

                        if (writes.Count > WorldFieldCapacity.MaxTransformTerms) {
                            errors.Add(item: $"{path}.then declares {writes.Count} writes, exceeding the {WorldFieldCapacity.MaxTransformTerms}-term ceiling.");
                        }

                        if (writes.Count == 0) {
                            errors.Add(item: $"{path}.then is empty.");
                        }

                        for (var c = 0; (c < conditions.Count); c++) {
                            RequireField(
                                name: conditions[c]?.Field,
                                path: $"{path}.when[{c}].field"
                            );

                            if (conditions[c] is { } condition) {
                                RequireScalarValue(
                                    path: $"{path}.when[{c}].value",
                                    value: condition.Value
                                );
                            }

                            if ((conditions[c] is { } definedCondition) && !Enum.IsDefined(value: definedCondition.Comparison)) {
                                errors.Add(item: $"{path}.when[{c}].comparison '{definedCondition.Comparison}' is unknown.");
                            }
                        }

                        for (var t = 0; (t < writes.Count); t++) {
                            RequireField(
                                name: writes[t]?.Field,
                                path: $"{path}.then[{t}].field"
                            );

                            if (writes[t] is { } write) {
                                RequireScalarValue(
                                    path: $"{path}.then[{t}].value",
                                    value: write.Value
                                );
                            }

                            if ((writes[t] is { } definedWrite) && !Enum.IsDefined(value: definedWrite.Op)) {
                                errors.Add(item: $"{path}.then[{t}].op '{definedWrite.Op}' is unknown.");
                            }
                        }

                        break;
                    }
                case WorldReaction.Emit emit:
                    RequireField(
                        name: emit.Field,
                        path: $"{path}.field"
                    );
                    RequireKeyedIntRow(
                        row: emit.Tag,
                        path: $"{path}.tag"
                    );

                    RequireScalarValue(
                        path: $"{path}.amount",
                        value: emit.Amount
                    );

                    break;
                case WorldReaction.Expose expose:
                    RequireField(
                        name: expose.Field,
                        path: $"{path}.field"
                    );
                    RequireKeyedIntRow(
                        row: expose.Row,
                        path: $"{path}.row"
                    );

                    RequireScalarValue(
                        path: $"{path}.value",
                        value: expose.Value
                    );

                    if (!Enum.IsDefined(value: expose.Comparison)) {
                        errors.Add(item: $"{path}.comparison '{expose.Comparison}' is unknown.");
                    }

                    break;
                case WorldReaction.Flow flow: {
                        RequireField(
                            name: flow.Field,
                            path: $"{path}.field"
                        );
                        RequireRate(
                            rate: flow.Rate,
                            path: $"{path}.rate"
                        );

                        var over = (flow.Over ?? []);
                        var overNames = new HashSet<string>(comparer: StringComparer.Ordinal);

                        for (var o = 0; (o < over.Count); o++) {
                            var overPath = $"{path}.over[{o}]";

                            RequireField(
                                name: over[o],
                                path: overPath
                            );

                            if (string.Equals(a: over[o], b: flow.Field, comparisonType: StringComparison.Ordinal)) {
                                errors.Add(item: $"{overPath} names '{over[o]}', the field flow itself transports; the field's own value already contributes without repeating it in over.");
                            } else if ((over[o] is { } overName) && !overNames.Add(item: overName)) {
                                errors.Add(item: $"{overPath} names '{over[o]}', duplicated within over.");
                            }
                        }

                        if (flow.SpillRow is { } spillRow) {
                            RequireScalarRow(
                                path: $"{path}.spillRow",
                                row: spillRow
                            );
                        }

                        break;
                    }
                default:
                    errors.Add(item: $"{path} is an unknown reaction kind.");
                    break;
            }
        }

        var paint = (fields.Paint ?? []);

        if (paint.Count > WorldFieldCapacity.MaxPaint) {
            errors.Add(item: $"fields.paint declares {paint.Count} rows, exceeding the {WorldFieldCapacity.MaxPaint}-row ceiling.");
        }

        var drawnFields = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < paint.Count); index++) {
            var path = $"fields.paint[{index}]";
            var row = paint[index];

            if (row is null) {
                errors.Add(item: $"{path} is null.");

                continue;
            }

            RequireField(
                name: row.Field,
                path: $"{path}.field"
            );

            switch (row) {
                case WorldLatticeFill.Rect rect:
                    if (
                        !FitsFixed(value: rect.Value) ||
                        !FitsFixed(value: rect.MinX) ||
                        !FitsFixed(value: rect.MinZ) ||
                        !FitsFixed(value: rect.MaxX) ||
                        !FitsFixed(value: rect.MaxZ) ||
                        (rect.MinX > rect.MaxX) ||
                        (rect.MinZ > rect.MaxZ)
                    ) {
                        errors.Add(item: $"{path} must carry a finite value and a finite min <= max rectangle.");
                    }

                    break;
                case WorldLatticeFill.Noise noise:
                    if (
                        !FitsFixed(value: noise.Value) ||
                        !float.IsFinite(f: noise.Threshold) ||
                        (noise.Threshold < 0f) ||
                        (noise.Threshold >= 1f)
                    ) {
                        errors.Add(item: $"{path} must carry a finite value and a threshold in [0, 1).");
                    }
                    if (noise.Frequency < 1) {
                        errors.Add(item: $"{path}.frequency must be at least 1 (noise-cell edge in lattice cells; was {noise.Frequency}).");
                    }
                    if ((noise.Octaves < 1) || (noise.Octaves > 4)) {
                        errors.Add(item: $"{path}.octaves must be in 1..4 (was {noise.Octaves}).");
                    }

                    break;
                case WorldLatticeFill.Draw draw:
                    if (!drawnFields.Add(item: row.Field)) {
                        errors.Add(item: $"{path} is a second draw fill on field '{row.Field}' — a lattice row draws one whole-field pass at a time through its own cursor and masks, so it carries at most one draw fill.");
                    }

                    if (!WorldGeneratorEngine.TryResolveSource(
                        generators: definition.Generators,
                        draw: new WorldDraw(Source: draw.Source, Generator: draw.Generator),
                        generator: out var drawSource,
                        reason: out var drawReason
                    )) {
                        errors.Add(item: $"{path} {drawReason}.");

                        break;
                    }

                    if (draw.Generator is { } inlineSource) {
                        ValidateSource(
                            errors: errors,
                            generator: inlineSource,
                            path: $"{path}.generator"
                        );
                    }

                    if (!WorldGeneratorEngine.TryCheckTargetKind(
                        source: drawSource.Source,
                        targetKind: CellKind.Fixed,
                        reason: out var kindReason
                    )) {
                        errors.Add(item: $"{path} {kindReason} — a lattice cell is a fixed value.");
                    }

                    var latticeSamples = ((((long)lattice.Width) * lattice.Depth) * lattice.Layers);
                    var drawnMasks = WorldDefinitionRows.FindStateRow(
                        rows: definition.State,
                        name: row.Field
                    )?.DrawnMasks;

                    ValidateDrawnMasks(
                        masks: drawnMasks,
                        errors: errors,
                        generator: drawSource,
                        path: $"state row '{row.Field}' drawnMasks"
                    );

                    if (
                        (latticeSamples > 0L) &&
                        !WorldGeneratorEngine.TryCheckBatchCapacity(
                            generator: drawSource,
                            masks: drawnMasks,
                            sampleCount: latticeSamples,
                            reason: out var batchReason
                        )
                    ) {
                        errors.Add(item: $"{path} {batchReason}.");
                    }

                    break;
                case WorldLatticeFill.Scatter scatter:
                    if (!FitsFixed(value: scatter.Value)) {
                        errors.Add(item: $"{path} must carry a finite value.");
                    }
                    if (scatter.Spacing < 2) {
                        errors.Add(item: $"{path}.spacing must be at least 2 cells (was {scatter.Spacing}).");
                    }
                    if ((scatter.Radius < 1) || ((2 * scatter.Radius) > scatter.Spacing)) {
                        errors.Add(item: $"{path}.radius must be at least 1 and at most spacing/2 (a disc never leaves its block; was {scatter.Radius} against spacing {scatter.Spacing}).");
                    }

                    break;
            }
        }
    }
}
