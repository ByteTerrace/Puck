using Puck.Assets.Documents;
using Puck.Cli.Bench;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.World;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Determinism;

/// <summary>
/// Records a determinism manifest's scenarios in this process. Each scenario's world is composed, admitted and booted
/// the way the game boots it, on a fresh <see cref="WorldServer"/>; its document-level hashes are taken once, its
/// seats are joined, and every tick its held intents are submitted, the authority steps as the live step shell steps
/// it, and the tick's hash vector is taken after the step, at the tick the replay tape samples.
/// </summary>
internal static class DeterminismRecorder {
    private static bool IsToken(string text) => (
        (text.Length > 0) &&
        !text.Any(predicate: static character => (char.IsWhiteSpace(c: character) || char.IsControl(c: character)))
    );
    private static bool TryReadDocument(string path, out byte[] document, out string error) {
        document = [];
        error = string.Empty;

        if (!path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".puck")) {
            document = File.ReadAllBytes(path: path);

            return true;
        }
        if (!WorldCompileCache.Shared.TryCompile(
            compiled: out var compiled,
            failure: out var failure,
            path: path
        )) {
            error = $"{path} does not compile: {string.Join(separator: "; ", values: failure!.Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code} {diagnostic.Message}"))}";

            return false;
        }
        if (compiled!.Document is not { } bytes) {
            error = $"{path} emits no single world document";

            return false;
        }

        document = bytes;

        return true;
    }
    // The document-level hashes, each recomputed here rather than read from the document, so a host that derives one
    // differently records its own value: the definition's fingerprint, the compiled world's key, every asset row's
    // and creation's canonical hash, and every creation's bake key.
    private static bool TryDocumentHashes(WorldDefinition authored, WorldDefinition definition, string catalogFingerprint, out List<DeterminismDocumentHash> hashes, out string error) {
        hashes = [];
        error = string.Empty;

        var header = CompiledWorld.HeaderFor(
            authored: authored,
            catalogFingerprint: catalogFingerprint,
            instanceIdentity: WorldDefinitionLoader.BootInstanceName
        );
        var directory = definition.DocumentDirectory;

        hashes.Add(item: new DeterminismDocumentHash(Name: "fingerprint", Value: WorldDefinitionFingerprint.Compute(definition: definition)));
        hashes.Add(item: new DeterminismDocumentHash(Name: "definition", Value: header.DefinitionHash.ToString()));
        hashes.Add(item: new DeterminismDocumentHash(Name: "catalog", Value: header.CatalogFingerprint));

        foreach (var row in definition.Patches) {
            if (!WorldAssetRowLoader.TryLoadPatch(document: out var document, documentDirectory: directory, error: out var loadError, row: row)) {
                error = $"patch '{row.Name}': {loadError}";

                return false;
            }

            hashes.Add(item: new DeterminismDocumentHash(Name: $"patch:{row.Name}", Value: SynthPatchCanonicalizer.Canonicalize(document: document!, source: row.Name).Hash));
        }
        foreach (var row in definition.Tunes) {
            if (!WorldAssetRowLoader.TryLoadTune(document: out var document, documentDirectory: directory, error: out var loadError, row: row)) {
                error = $"tune '{row.Name}': {loadError}";

                return false;
            }

            hashes.Add(item: new DeterminismDocumentHash(Name: $"tune:{row.Name}", Value: AudioCanonicalizer.Canonicalize(document: document!, source: row.Name).Hash));
        }
        foreach (var row in (definition.Music ?? [])) {
            if (!WorldAssetRowLoader.TryLoadMusic(document: out var document, documentDirectory: directory, error: out var loadError, row: row)) {
                error = $"music '{row.Name}': {loadError}";

                return false;
            }

            hashes.Add(item: new DeterminismDocumentHash(Name: $"music:{row.Name}", Value: MusicCanonicalizer.Canonicalize(document: document!, source: row.Name).Hash));
        }
        foreach (var row in (definition.Tables ?? [])) {
            if (!WorldAssetRowLoader.TryLoadTable(document: out var document, documentDirectory: directory, error: out var loadError, row: row)) {
                error = $"table '{row.Name}': {loadError}";

                return false;
            }

            hashes.Add(item: new DeterminismDocumentHash(Name: $"table:{row.Name}", Value: TableCanonicalizer.Canonicalize(document: document!, source: row.Name).Hash));
        }
        foreach (var prototype in definition.Creations) {
            hashes.Add(item: new DeterminismDocumentHash(Name: $"creation:{prototype.Id}", Value: CreationCanonicalizer.Canonicalize(document: prototype.Document, source: prototype.Id).Hash));
        }
        foreach (var request in WorldBakeStore.RequestsOf(definition: definition, quality: WorldBakeChunk.Quality)) {
            var name = $"bake:{request.PrototypeId}";

            if (!hashes.Any(predicate: hash => (hash.Name == name))) {
                hashes.Add(item: new DeterminismDocumentHash(Name: name, Value: request.Key.Pin.Hex));
            }
        }

        if (hashes.FirstOrDefault(predicate: static hash => (!IsToken(text: hash.Name) || !IsToken(text: hash.Value))) is { } unspellable) {
            error = $"document hash '{unspellable.Name}' cannot be written as one token; rename the row";

            return false;
        }

        return true;
    }
    // A cell write's raw value for its row's kind, parsed exactly.
    private static bool TryCellValue(WorldDefinition definition, DeterminismCellWrite cell, out long value, out string error) {
        value = 0L;
        error = string.Empty;

        if (WorldDefinitionRows.FindStateRow(name: cell.Row, rows: definition.State) is not { } row) {
            error = $"the world declares no state row '{cell.Row}'";

            return false;
        }

        switch (row.Kind) {
            case CellKind.Int when long.TryParse(s: cell.Value, style: System.Globalization.NumberStyles.AllowLeadingSign, provider: System.Globalization.CultureInfo.InvariantCulture, result: out value):
                return true;
            case CellKind.Fixed when FixedQ4816.TryParse(provider: System.Globalization.CultureInfo.InvariantCulture, result: out var fixedValue, s: cell.Value):
                value = fixedValue.Value;

                return true;
            case CellKind.Bool when (cell.Value is "true" or "false"):
                value = ((cell.Value == "true") ? 1L : 0L);

                return true;
            default:
                error = $"'{cell.Value}' is not a value of row '{cell.Row}', a {row.Kind} row; a scenario writes Int, Fixed and Bool cells";

                return false;
        }
    }
    /// <summary>Reads the completed tick's attestation vector.</summary>
    /// <param name="server">The authority after a completed step.</param>
    /// <returns>The hashes in stream component order.</returns>
    public static ulong[] Vector(WorldServer server) {
        var tick = (server.NextInputTick - 1UL);
        var vector = new ulong[DeterminismStream.Components.Count];
        var index = 0;

        foreach (var component in DeterminismStream.TickComponents) {
            vector[index++] = WorldStateHashComposition.Compose(
                order: [component],
                seed: 0UL,
                server: server,
                tick: tick
            );
        }

        vector[index++] = WorldReplaySnapshot.HashState(population: server.Population);
        vector[index] = WorldStateHashComposition.HashAuthoritative(
            server: server,
            tick: tick
        );

        return vector;
    }

    /// <summary>Loads a scenario's world the way the game boots it: a <c>.puck</c> source compiled, then composed and
    /// admitted beside its own directory.</summary>
    /// <param name="path">The world document or source.</param>
    /// <param name="authored">The composed, undrawn definition a compiled world is keyed by.</param>
    /// <param name="definition">The admitted, drawn definition the authority boots.</param>
    /// <param name="error">Why the world could not be loaded, or empty.</param>
    /// <returns><see langword="true"/> when the world loaded.</returns>
    public static bool TryLoadWorld(string path, out WorldDefinition? authored, out WorldDefinition? definition, out string error) {
        authored = null;
        definition = null;

        var catalog = CliWorldVocabulary.EnsureInstalled();

        if (
            !TryReadDocument(document: out var document, error: out error, path: path) ||
            !WorldSourceLoader.TryReadAuthored(
                authored: out authored,
                catalog: catalog,
                catalogFingerprint: catalog.CompositionFingerprint,
                document: document,
                path: path,
                reason: out error
            ) ||
            !WorldSourceLoader.TryLoadForAdmission(
                admission: out var admission,
                catalog: catalog,
                catalogFingerprint: catalog.CompositionFingerprint,
                document: document,
                path: path,
                reason: out error
            )
        ) {
            return false;
        }

        definition = admission!.Definition;

        return true;
    }
    /// <summary>Records one scenario.</summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="record">The record, or <see langword="null"/> when the scenario could not run.</param>
    /// <param name="error">Why the scenario could not run, or empty.</param>
    /// <returns><see langword="true"/> when every tick was recorded.</returns>
    public static bool TryRecord(DeterminismScenario scenario, out DeterminismScenarioRecord? record, out string error) {
        record = null;

        var catalog = CliWorldVocabulary.EnsureInstalled();

        if (!TryLoadWorld(authored: out var authored, definition: out var loaded, error: out error, path: scenario.World)) {
            return false;
        }

        var definition = loaded!;

        if (definition.SimulationRateHz <= 0) {
            error = $"{scenario.World} authors no stepping simulation rate";

            return false;
        }
        if (!TryDocumentHashes(
            authored: authored!,
            catalogFingerprint: catalog.CompositionFingerprint,
            definition: definition,
            error: out error,
            hashes: out var documents
        )) {
            return false;
        }

        var ordinals = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var ordinal = 0; (ordinal < definition.Channels.Count); ordinal++) {
            ordinals[definition.Channels[ordinal].Name] = ordinal;
        }
        foreach (var intent in scenario.Intents) {
            if (intent.Channels.FirstOrDefault(predicate: channel => !ordinals.ContainsKey(key: channel.Channel)) is { Channel: { } unknown }) {
                error = $"{scenario.Name}: the world declares no channel '{unknown}'";

                return false;
            }
        }

        using var host = WorldBenchServer.Boot(
            catalog: catalog,
            definition: definition,
            documentPath: scenario.World
        );
        var server = host.Server;

        foreach (var seat in scenario.Seats) {
            var principal = Principal.Seat(slot: seat);

            if (!server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: principal,
                Slot: principal.Index,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted) {
                error = $"{scenario.Name}: seat {seat} is not admitted";

                return false;
            }
        }

        var writes = new List<(int Tick, WorldMutation Mutation)>(capacity: scenario.Cells.Count);

        foreach (var cell in scenario.Cells) {
            if (!TryCellValue(cell: cell, definition: definition, error: out var cellError, value: out var value)) {
                error = $"{scenario.Name}: {cellError}";

                return false;
            }

            writes.Add(item: (cell.Tick, new WorldMutation.UpsertStateCell(
                Key: cell.Key,
                Kind: WorldDocumentWriteKind.Set,
                Principal: Principal.Console,
                Row: cell.Row,
                Value: value
            )));
        }

        var stepTicks = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));
        var ticks = new List<ulong[]>(capacity: scenario.Ticks);

        for (var tick = 1; (tick <= scenario.Ticks); tick++) {
            foreach (var (_, mutation) in writes.Where(predicate: write => (write.Tick == tick))) {
                server.EnqueueMutation(mutation: mutation);
            }
            foreach (var intent in scenario.Intents.Where(predicate: intent => ((intent.From <= tick) && (tick <= intent.Through)))) {
                if (server.Body(index: intent.Body) is not { } body) {
                    error = $"{scenario.Name}: tick {tick} drives body {intent.Body}, which is not active";

                    return false;
                }

                var player = default(PlayerIntent);

                foreach (var (channel, value) in intent.Channels) {
                    player = player.WithChannel(ordinal: ordinals[channel], value: value);
                }

                body.SubmitIntent(intent: in player);
            }

            server.Advance(stepTicks: stepTicks);
            server.EnforceJournalDepth();
            ticks.Add(item: Vector(server: server));
        }

        record = new DeterminismScenarioRecord(
            Documents: documents,
            Name: scenario.Name,
            Ticks: ticks
        );

        return true;
    }
    /// <summary>Records every scenario of a manifest, in order.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="stream">The stream, or <see langword="null"/> when a scenario could not run.</param>
    /// <param name="error">Which scenario could not run and why, or empty.</param>
    /// <returns><see langword="true"/> when every scenario was recorded.</returns>
    public static bool TryRecord(DeterminismManifest manifest, out DeterminismStream? stream, out string error) {
        stream = null;
        error = string.Empty;

        var scenarios = new List<DeterminismScenarioRecord>(capacity: manifest.Scenarios.Count);

        foreach (var scenario in manifest.Scenarios) {
            if (!TryRecord(error: out var scenarioError, record: out var record, scenario: scenario)) {
                error = $"scenario '{scenario.Name}': {scenarioError}";

                return false;
            }

            scenarios.Add(item: record!);
        }

        stream = new DeterminismStream(
            ManifestPin: manifest.Pin.ToString(),
            Scenarios: scenarios
        );

        return true;
    }
}
