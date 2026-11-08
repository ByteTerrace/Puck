using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Embeddings;
using Puck.World.Server;
using Puck.Maths;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;

namespace Puck.Cli.Transpiler;

/// <summary>Implements the <c>puck embed</c> command and its <c>probe</c> subcommand.</summary>
public static class EmbedCommand {
    private const string LockVerb = "embed lock";
    private const string ProbeVerb = "embed probe";

    private sealed record ProviderOptions(Option<string?> Provider, Option<string?> Settings) {
        public static ProviderOptions Create() => new(
            Provider: new Option<string?>(name: "--provider") { Description = "Installed embedding provider type key, such as embedding.fixture or embedding.azure-openai; defaults to the single installed type." },
            Settings: new Option<string?>(name: "--settings") { Description = "JSON object file with provider settings. Model, revision and dimensions come from the authored embedding space." }
        );
        public IEnumerable<Option> All => [Provider, Settings];
    }
    /// <summary>Creates <c>puck embed</c> and its <c>lock</c> and <c>probe</c> verbs.</summary>
    /// <returns>The configured command tree.</returns>
    public static Command Create() {
        var embedCommand = new Command(
            description: "Resolve authored embed(...) text into committed .embeddings.json lock files, or probe their similarity rankings.",
            name: "embed"
        ) { CreateLockSubcommand(), CreateProbeSubcommand() };

        return embedCommand;
    }

    private static Command CreateLockSubcommand() {
        var pathArgument = new Argument<string>(name: "path") {
            Description = "The .puck root file or directory of roots to embed.",
        };
        var checkOption = CliOptions.Check(description: "Verify that all authored texts are locked and fresh without contacting a provider.");
        var providers = ProviderOptions.Create();
        var batchSizeOption = CliOptions.Count(defaultValue: 64, description: "Number of texts per embedding request batch (1..2048, default 64).", maximum: 2048, minimum: 1, name: "--batch-size");
        var lockCommand = new Command(
            description: "Resolve authored embed(...) text into committed .embeddings.json lock files.",
            name: "lock"
        ) { pathArgument, checkOption, batchSizeOption };

        foreach (var option in providers.All) {
            lockCommand.Options.Add(item: option);
        }

        lockCommand.SetAction(action: async (parseResult, cancellationToken) => await ExecuteEmbedAsync(
            batchSize: parseResult.GetValue(option: batchSizeOption),
            cancellationToken: cancellationToken,
            check: parseResult.GetValue(option: checkOption),
            settingsPath: parseResult.GetValue(option: providers.Settings),
            path: parseResult.GetRequiredValue(argument: pathArgument),
            provider: parseResult.GetValue(option: providers.Provider)
        ).ConfigureAwait(continueOnCapturedContext: false));

        return lockCommand;
    }

    private static Command CreateProbeSubcommand() {
        var pathArgument = new Argument<string>(name: "path") {
            Description = "The .puck root file containing the embedding space.",
        };
        var textArgument = new Argument<string>(name: "text") {
            Description = "The query text to probe against locked vectors.",
        };
        var spaceOption = new Option<string?>(name: "--space") {
            Description = "The target embedding space name (optional if only one space is declared).",
        };
        var topOption = CliOptions.Count(defaultValue: 5, description: "Maximum number of similarity matches to display (default 5).", minimum: 1, name: "--top");
        var providers = ProviderOptions.Create();
        var probeCommand = new Command(
            description: "Rank similarity of a query text against locked vectors.",
            name: "probe"
        ) { pathArgument, textArgument, spaceOption, topOption };

        foreach (var option in providers.All) {
            probeCommand.Options.Add(item: option);
        }

        probeCommand.SetAction(action: async (parseResult, cancellationToken) => await ExecuteProbeAsync(
            cancellationToken: cancellationToken,
            settingsPath: parseResult.GetValue(option: providers.Settings),
            path: parseResult.GetRequiredValue(argument: pathArgument),
            provider: parseResult.GetValue(option: providers.Provider),
            space: parseResult.GetValue(option: spaceOption),
            text: parseResult.GetRequiredValue(argument: textArgument),
            top: parseResult.GetValue(option: topOption)
        ).ConfigureAwait(continueOnCapturedContext: false));

        return probeCommand;
    }

    private static async Task<int> ExecuteEmbedAsync(
        string path,
        bool check,
        string? provider,
        string? settingsPath,
        int batchSize,
        CancellationToken cancellationToken
    ) {
        var fullPath = Path.GetFullPath(path: path);
        List<string> puckFiles = [];

        if (Directory.Exists(path: fullPath)) {
            puckFiles.AddRange(collection: Directory.GetFiles(path: fullPath, searchOption: SearchOption.AllDirectories, searchPattern: "*.puck").Order(comparer: StringComparer.Ordinal));
        } else if (File.Exists(path: fullPath)) {
            puckFiles.Add(item: fullPath);
        } else {
            return CliExit.Refuse(verb: LockVerb, what: CliPaths.ToDisplay(fullPath: fullPath), why: "does not exist.");
        }

        var overallExitCode = 0;
        var locked = 0;

        foreach (var puckFile in puckFiles) {
            if (!TryDiscoverSpacesAndTexts(
                failure: out var failure,
                puckFilePath: puckFile,
                spaces: out var spaces,
                textsBySpace: out var textsBySpace
            )) {
                overallExitCode = CliExit.Refuse(verb: LockVerb, what: CliPaths.ToDisplay(fullPath: puckFile), why: failure);
                continue;
            }

            if ((spaces.Count == 0) && (textsBySpace.Count == 0)) {
                continue;
            }

            locked++;

            var lockPath = EmbeddingLock.DeriveLockPath(sourcePath: puckFile);
            var lockFile = (EmbeddingLock.TryLoad(rootSourcePath: puckFile) ?? new EmbeddingLock());

            var missingCount = 0;
            var staleCount = 0;
            var unusedCount = 0;

            var declaredSpaceNames = spaces.Select(selector: s => s.Name).ToHashSet(comparer: StringComparer.Ordinal);

            // Pruning: check for unused spaces
            foreach (var spaceName in lockFile.Spaces.Keys) {
                if (!declaredSpaceNames.Contains(value: spaceName)) {
                    unusedCount++;
                }
            }

            if (!check) {
                lockFile.PruneSpaces(usedSpaceNames: declaredSpaceNames);
            }

            // For each declared space, check entries
            foreach (var space in spaces) {
                textsBySpace.TryGetValue(key: space.Name, value: out var requiredTexts);
                requiredTexts ??= [];

                if (!lockFile.Spaces.TryGetValue(key: space.Name, value: out var lockSpace)) {
                    lockSpace = new EmbeddingLockSpace(identity: space.Identity);

                    if (!check) {
                        lockFile.Spaces[space.Name] = lockSpace;
                    }
                }

                // Check if space parameters changed (stale)
                if (lockSpace.Identity != space.Identity) {
                    staleCount += lockSpace.Entries.Count;

                    if (!check) {
                        lockSpace.Identity = space.Identity;
                        lockSpace.Entries.Clear();
                    }
                }

                // Check unused entries in this space
                foreach (var (_, entry) in lockSpace.Entries) {
                    if (!requiredTexts.Contains(value: entry.Text)) {
                        unusedCount++;
                    }
                }

                if (!check) {
                    lockFile.PruneEntries(spaceName: space.Name, usedTexts: requiredTexts);
                }

                // Check missing entries
                List<string> textsToEmbed = [];

                foreach (var text in requiredTexts) {
                    if (!lockFile.TryGet(spaceName: space.Name, text: text, vectorBase64Url: out _)) {
                        missingCount++;
                        textsToEmbed.Add(item: text);
                    }
                }

                if (check) {
                    continue;
                }

                if (textsToEmbed.Count > 0) {
                    var identity = space.Identity;

                    IWorldEmbeddingSource generator;

                    try {
                        generator = ResolveGenerator(
                            settingsPath: settingsPath,
                            identity: identity,
                            provider: provider
                        );
                    } catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or ArgumentException) {
                        return CliExit.Refuse(verb: LockVerb, what: $"space '{space.Name}'", why: $"its provider could not start: {ex.Message}");
                    }

                    using (generator) {
                        var batches = EmbeddingBatcher.Batch(batchSize: batchSize, items: textsToEmbed);

                        foreach (var batch in batches) {
                            IReadOnlyList<EmbeddingAnswer> embeddings;

                            try {
                                embeddings = await generator.EmbedAsync(
                                    cancellationToken: cancellationToken,
                                    texts: batch
                                ).ConfigureAwait(continueOnCapturedContext: false);
                            } catch (Exception ex) when (ex is not OperationCanceledException) {
                                return CliExit.Refuse(verb: LockVerb, what: $"space '{space.Name}'", why: $"the embedding provider failed: {ex.Message}");
                            }

                            var embeddingList = embeddings.ToList();

                            if (embeddingList.Count != batch.Count) {
                                return CliExit.Refuse(verb: LockVerb, what: $"space '{space.Name}'", why: $"the provider returned {embeddingList.Count} embeddings for {batch.Count} texts.");
                            }

                            for (var i = 0; (i < batch.Count); i++) {
                                var srcText = batch[i];
                                var emb = embeddingList[i];

                                if (emb.Vector is not { } vector) {
                                    return CliExit.Refuse(verb: LockVerb, what: $"text \"{srcText}\"", why: (emb.Refusal ?? "its embedding was refused."));
                                }

                                lockFile.SetEntry(identity: identity, spaceName: space.Name, text: srcText, vectorBase64Url: vector.ToBase64Url());
                            }
                        }
                    }
                }
            }

            if (check) {
                if ((missingCount > 0) || (staleCount > 0) || (unusedCount > 0)) {
                    Console.Error.WriteLine(
                        value: $"check failed for '{puckFile}': {missingCount} missing, {staleCount} stale, {unusedCount} unused."
                    );
                    overallExitCode = 1;
                } else {
                    Console.WriteLine(value: $"check passed: '{puckFile}' lock is up to date.");
                }
                continue;
            }

            bool written;

            try {
                written = lockFile.Write(lockPath: lockPath);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                overallExitCode = CliExit.Refuse(verb: LockVerb, what: CliPaths.ToDisplay(fullPath: lockPath), why: $"the lock file could not be read or replaced: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
                continue;
            }

            if (written) {
                Console.WriteLine(value: $"Updated lock file '{lockPath}'.");
            } else {
                Console.WriteLine(value: $"Lock file '{lockPath}' is up to date.");
            }
        }

        if ((locked == 0) && (overallExitCode == 0)) {
            return CliExit.Empty(verb: LockVerb, selection: CliPaths.ToDisplay(fullPath: fullPath), why: ((puckFiles.Count == 0) ? "holds no .puck source" : "declares no embedding space and no embed(...) text"));
        }

        return overallExitCode;
    }
    private static async Task<int> ExecuteProbeAsync(
        string path,
        string text,
        string? space,
        int top,
        string? provider,
        string? settingsPath,
        CancellationToken cancellationToken
    ) {
        var fullPath = Path.GetFullPath(path: path);

        if (!File.Exists(path: fullPath)) {
            return CliExit.Refuse(verb: ProbeVerb, what: CliPaths.ToDisplay(fullPath: fullPath), why: "does not exist.");
        }

        var lockFile = EmbeddingLock.TryLoad(rootSourcePath: fullPath);

        if ((lockFile is null) || (lockFile.Spaces.Count == 0)) {
            return CliExit.Refuse(verb: ProbeVerb, what: CliPaths.ToDisplay(fullPath: fullPath), why: "has no embedding lock file; run puck embed lock first.");
        }

        string resolvedSpaceName;

        if (!string.IsNullOrEmpty(value: space)) {
            resolvedSpaceName = space;
        } else if (lockFile.Spaces.Count == 1) {
            resolvedSpaceName = lockFile.Spaces.Keys.First();
        } else {
            return CliExit.Refuse(verb: ProbeVerb, what: "--space", why: "the lock file declares several spaces; name the one to probe.");
        }

        if (!lockFile.Spaces.TryGetValue(key: resolvedSpaceName, value: out var lockSpace)) {
            return CliExit.Refuse(verb: ProbeVerb, what: $"--space {resolvedSpaceName}", why: "names no space in the lock file.");
        }

        StateVector queryVector;

        if (lockFile.TryGet(spaceName: resolvedSpaceName, text: text, vectorBase64Url: out var existingVector)) {
            if (!StateVector.TryParseBase64Url(dimensions: lockSpace.Identity.Dimensions, error: out var err, text: existingVector, vector: out var vec)) {
                return CliExit.Refuse(verb: ProbeVerb, what: $"space '{resolvedSpaceName}'", why: $"its locked vector is corrupt: {err}");
            }
            queryVector = vec;
        } else {
            var identity = lockSpace.Identity;

            IWorldEmbeddingSource generator;

            try {
                generator = ResolveGenerator(
                    settingsPath: settingsPath,
                    identity: identity,
                    provider: provider
                );
            } catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or ArgumentException) {
                return CliExit.Refuse(verb: ProbeVerb, what: $"space '{resolvedSpaceName}'", why: $"its provider could not start: {ex.Message}");
            }

            using (generator) {
                var embs = await generator.EmbedAsync(
                    cancellationToken: cancellationToken,
                    texts: [text]
                ).ConfigureAwait(continueOnCapturedContext: false);

                var embList = embs.ToList();

                if ((embList.Count != 1) || (embList[0].Vector is not { } vec)) {
                    return CliExit.Refuse(verb: ProbeVerb, what: "the query text", why: (embList.FirstOrDefault().Refusal ?? "the provider returned no query vector."));
                }
                queryVector = vec;
            }
        }

        var results = new List<(string Label, long Similarity, long Dot)>();
        var querySpan = queryVector.Components;

        foreach (var entry in lockSpace.Entries.Values) {
            if (StateVector.TryParseBase64Url(dimensions: lockSpace.Identity.Dimensions, error: out _, text: entry.Vector, vector: out var targetVec)) {
                var targetSpan = targetVec.Components;
                var sim = SignedByteVectorFunctions.CosineQ16(left: querySpan, right: targetSpan);
                var dot = SignedByteVectorFunctions.Dot(left: querySpan, right: targetSpan);

                results.Add(item: (Label: entry.Text, Similarity: sim, Dot: dot));
            }
        }

        results.Sort(comparison: static (a, b) => b.Similarity.CompareTo(value: a.Similarity));

        var displayCount = Math.Min(val1: top, val2: results.Count);

        Console.WriteLine(value: $"Ranked similarity in space '{resolvedSpaceName}' (top {displayCount}):");

        for (var i = 0; (i < displayCount); i++) {
            var (label, similarity, dot) = results[i];
            var simFloat = (similarity / 65536.0);

            Console.WriteLine(value: $"  {(i + 1),2}. similarity: {simFloat:F4}  dot: {dot,6}  \"{label}\"");
        }

        return 0;
    }
    private static IWorldEmbeddingSource ResolveGenerator(EmbeddingIdentity identity, string? provider, string? settingsPath) {
        var type = CliExtensions.Installed.Select<WorldExtensionEmbeddingProviderType>(key: provider, purpose: "embedding provider");
        var settings = (settingsPath is null ? new JsonObject() : JsonNode.Parse(File.ReadAllText(path: settingsPath)) as JsonObject)
            ?? throw new ArgumentException(message: "Embedding settings must be a JSON object.");

        settings["model"] = identity.Model;
        settings["revision"] = identity.Revision;
        settings["dimensions"] = identity.Dimensions;
        var configuration = JsonSerializer.SerializeToElement(value: settings);
        var configured = type.Create(configuration);

        try {
            var source = configured.BindEmbedding(settings: configuration);

            if (source.Identity != identity) {
                source.Dispose();
                throw new InvalidOperationException(message: "The embedding provider returned a different identity from the authored space.");
            }
            return new OwnedEmbeddingSource(provider: configured, source: source);
        } catch {
            configured.Dispose();
            throw;
        }
    }

    private sealed class OwnedEmbeddingSource(IWorldConfiguredEmbeddingProvider provider, IWorldEmbeddingSource source) : IWorldEmbeddingSource {
        public EmbeddingIdentity Identity => source.Identity;
        public Task<IReadOnlyList<EmbeddingAnswer>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) => source.EmbedAsync(texts, cancellationToken);
        public void Dispose() {
            try { source.Dispose(); } finally { provider.Dispose(); }
        }
    }
    private sealed record DiscoveredSpace(string Name, EmbeddingIdentity Identity);

    private static bool TryDiscoverSpacesAndTexts(
        string puckFilePath,
        out IReadOnlyList<DiscoveredSpace> spaces,
        out IReadOnlyDictionary<string, HashSet<string>> textsBySpace,
        [NotNullWhen(false)] out string? failure
    ) {
        spaces = [];
        textsBySpace = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
        failure = null;

        var loweringDiags = new DiagnosticBag();
        var lowerResult = WorldCompiler.CompileFile(
            diagnostics: loweringDiags,
            imports: ImportHandling.Ignore,
            path: puckFilePath
        );

        if (lowerResult.Document is null) {
            failure = "Syntax errors in .puck source.";
            return false;
        }

        var discoveredEmbeddings = lowerResult.DiscoveredEmbeddings;
        var realErrors = loweringDiags.Where(predicate: static d => ((d.Severity == DiagnosticSeverity.Error) &&
            (d.Code != PuckDiagnosticCodes.EmbeddingLockMissing) &&
            (d.Code != PuckDiagnosticCodes.EmbeddingLockStale))).ToList();

        if (realErrors.Count > 0) {
            failure = ($"Lowering errors in '{puckFilePath}': " + string.Join(separator: "; ", values: realErrors.Select(selector: static e => $"{e.Code}: {e.Message}")));
            return false;
        }

        var discoveredSpaces = new List<DiscoveredSpace>();

        if ((lowerResult.Json is JsonObject root) &&
            (root["state"] is JsonObject stateObj) &&
            (stateObj["spaces"] is JsonArray spacesArr)) {
            foreach (var spNode in spacesArr) {
                if (spNode is JsonObject spObj) {
                    discoveredSpaces.Add(item: new DiscoveredSpace(
                        Identity: WorldDocumentEmitter.ReadSpaceIdentity(space: spObj),
                        Name: (spObj["name"]?.ToString() ?? "")
                    ));
                }
            }
        }

        spaces = discoveredSpaces;
        textsBySpace = discoveredEmbeddings;
        return true;
    }
}
