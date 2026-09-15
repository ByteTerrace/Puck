using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Puck.Embeddings;
using Puck.Maths;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Sql;

namespace Puck.Cli.Transpiler;

/// <summary>Implements the <c>puck embed</c> command and its <c>probe</c> subcommand.</summary>
public static class EmbedCommand {
    /// <summary>Creates the <c>embed</c> CLI command definition.</summary>
    /// <returns>The configured command tree.</returns>
    public static Command Create() {
        var pathArgument = new Argument<string>(name: "path") {
            Description = "The .puck root file or directory of roots to embed.",
        };

        var checkOption = new Option<bool>(name: "--check") {
            Description = "Verify that all authored texts are locked and fresh without contacting a provider.",
        };

        var providerOption = new Option<string?>(name: "--provider") {
            Description = "Provider kind: 'fixture' or 'openai-compatible'. Defaults to fixture for puck-fixture models.",
        };

        var endpointOption = new Option<string?>(name: "--endpoint") {
            Description = "OpenAI-compatible HTTP endpoint URI.",
        };

        var apiKeyEnvOption = new Option<string?>(name: "--api-key-env") {
            Description = "Name of the environment variable containing the API key.",
        };

        var apiKeyHeaderOption = new Option<string?>(name: "--api-key-header") {
            Description = "Header name for the API key (default: 'Authorization' with 'Bearer'; 'api-key' sends raw key).",
        };

        var omitDimensionsOption = new Option<bool>(name: "--omit-dimensions") {
            Description = "When true, omits the dimensions field from embedding request payloads.",
        };

        var batchSizeOption = new Option<int>(name: "--batch-size") {
            DefaultValueFactory = static _ => 64,
            Description = "Number of texts per embedding request batch (1..2048, default 64).",
        };

        var timeoutSecondsOption = new Option<int>(name: "--timeout-seconds") {
            DefaultValueFactory = static _ => 60,
            Description = "Request timeout in seconds (default 60).",
        };

        var embedCommand = new Command(
            description: "Resolve authored embed(...) text into committed .embeddings.json lock files.",
            name: "embed"
        ) {
            pathArgument,
            checkOption,
            providerOption,
            endpointOption,
            apiKeyEnvOption,
            apiKeyHeaderOption,
            omitDimensionsOption,
            batchSizeOption,
            timeoutSecondsOption,
        };

        var probeCommand = CreateProbeSubcommand();

        embedCommand.Add(command: probeCommand);

        embedCommand.SetAction(action: async (parseResult, cancellationToken) => {
            var path = parseResult.GetRequiredValue(argument: pathArgument);
            var check = parseResult.GetValue(option: checkOption);
            var provider = parseResult.GetValue(option: providerOption);
            var endpoint = parseResult.GetValue(option: endpointOption);
            var apiKeyEnv = parseResult.GetValue(option: apiKeyEnvOption);
            var apiKeyHeader = parseResult.GetValue(option: apiKeyHeaderOption);
            var omitDimensions = parseResult.GetValue(option: omitDimensionsOption);
            var batchSize = parseResult.GetValue(option: batchSizeOption);
            var timeoutSeconds = parseResult.GetValue(option: timeoutSecondsOption);

            return await ExecuteEmbedAsync(
                apiKeyEnv: apiKeyEnv,
                apiKeyHeader: apiKeyHeader,
                batchSize: batchSize,
                cancellationToken: cancellationToken,
                check: check,
                endpoint: endpoint,
                omitDimensions: omitDimensions,
                path: path,
                provider: provider,
                timeoutSeconds: timeoutSeconds
            ).ConfigureAwait(continueOnCapturedContext: false);
        });

        return embedCommand;
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

        var againstOption = new Option<string?>(name: "--against") {
            Description = "Optional table name to rank similarity against (defaults to all locked texts in the space).",
        };

        var topOption = new Option<int>(name: "--top") {
            DefaultValueFactory = static _ => 5,
            Description = "Maximum number of similarity matches to display (default 5).",
        };

        var providerOption = new Option<string?>(name: "--provider");
        var endpointOption = new Option<string?>(name: "--endpoint");
        var apiKeyEnvOption = new Option<string?>(name: "--api-key-env");
        var apiKeyHeaderOption = new Option<string?>(name: "--api-key-header");
        var omitDimensionsOption = new Option<bool>(name: "--omit-dimensions");
        var timeoutSecondsOption = new Option<int>(name: "--timeout-seconds") {
            DefaultValueFactory = static _ => 60,
        };

        var probeCommand = new Command(
            description: "Rank similarity of a query text against locked vectors.",
            name: "probe"
        ) {
            pathArgument,
            textArgument,
            spaceOption,
            againstOption,
            topOption,
            providerOption,
            endpointOption,
            apiKeyEnvOption,
            apiKeyHeaderOption,
            omitDimensionsOption,
            timeoutSecondsOption,
        };

        probeCommand.SetAction(action: async (parseResult, cancellationToken) => {
            var path = parseResult.GetRequiredValue(argument: pathArgument);
            var text = parseResult.GetRequiredValue(argument: textArgument);
            var space = parseResult.GetValue(option: spaceOption);
            var against = parseResult.GetValue(option: againstOption);
            var top = parseResult.GetValue(option: topOption);
            var provider = parseResult.GetValue(option: providerOption);
            var endpoint = parseResult.GetValue(option: endpointOption);
            var apiKeyEnv = parseResult.GetValue(option: apiKeyEnvOption);
            var apiKeyHeader = parseResult.GetValue(option: apiKeyHeaderOption);
            var omitDimensions = parseResult.GetValue(option: omitDimensionsOption);
            var timeoutSeconds = parseResult.GetValue(option: timeoutSecondsOption);

            return await ExecuteProbeAsync(
                against: against,
                apiKeyEnv: apiKeyEnv,
                apiKeyHeader: apiKeyHeader,
                cancellationToken: cancellationToken,
                endpoint: endpoint,
                omitDimensions: omitDimensions,
                path: path,
                provider: provider,
                space: space,
                text: text,
                timeoutSeconds: timeoutSeconds,
                top: top
            ).ConfigureAwait(continueOnCapturedContext: false);
        });

        return probeCommand;
    }

    private static async Task<int> ExecuteEmbedAsync(
        string path,
        bool check,
        string? provider,
        string? endpoint,
        string? apiKeyEnv,
        string? apiKeyHeader,
        bool omitDimensions,
        int batchSize,
        int timeoutSeconds,
        CancellationToken cancellationToken
    ) {
        var fullPath = Path.GetFullPath(path: path);
        List<string> puckFiles = [];

        if (Directory.Exists(path: fullPath)) {
            puckFiles.AddRange(collection: Directory.GetFiles(path: fullPath, searchPattern: "*.puck", searchOption: SearchOption.AllDirectories));
        } else if (File.Exists(path: fullPath)) {
            puckFiles.Add(item: fullPath);
        } else {
            Console.Error.WriteLine(value: $"error: Path '{path}' does not exist.");
            return 2;
        }

        var overallExitCode = 0;

        foreach (var puckFile in puckFiles) {
            if (!TryDiscoverSpacesAndTexts(
                failure: out var failure,
                puckFilePath: puckFile,
                spaces: out var spaces,
                textsBySpace: out var textsBySpace
            )) {
                Console.Error.WriteLine(value: $"error: Failed to analyze '{puckFile}': {failure}");
                overallExitCode = 1;
                continue;
            }

            if (spaces.Count == 0 && textsBySpace.Count == 0) {
                continue;
            }

            var lockPath = EmbeddingLock.DeriveLockPath(sourcePath: puckFile);
            var lockFile = (EmbeddingLock.TryLoad(rootSourcePath: puckFile) ?? new EmbeddingLock());

            var missingCount = 0;
            var staleCount = 0;
            var unusedCount = 0;

            var declaredSpaceNames = spaces.Select(selector: s => s.Name).ToHashSet(comparer: StringComparer.Ordinal);

            // Pruning: check for unused spaces
            foreach (var spaceName in lockFile.Spaces.Keys.ToList()) {
                if (!declaredSpaceNames.Contains(value: spaceName)) {
                    unusedCount++;
                    if (!check) {
                        lockFile.Spaces.Remove(key: spaceName);
                    }
                }
            }

            // For each declared space, check entries
            foreach (var space in spaces) {
                textsBySpace.TryGetValue(key: space.Name, value: out var requiredTexts);
                requiredTexts ??= [];

                if (!lockFile.Spaces.TryGetValue(key: space.Name, value: out var lockSpace)) {
                    lockSpace = new EmbeddingLockSpace(
                        dimensions: space.Dimensions,
                        model: space.Model,
                        revision: space.Revision
                    );

                    if (!check) {
                        lockFile.Spaces[space.Name] = lockSpace;
                    }
                }

                // Check if space parameters changed (stale)
                var isSpaceStale = (
                    lockSpace.Dimensions != space.Dimensions ||
                    !string.Equals(a: lockSpace.Model, b: space.Model, comparisonType: StringComparison.Ordinal) ||
                    !string.Equals(a: lockSpace.Revision, b: space.Revision, comparisonType: StringComparison.Ordinal)
                );

                if (isSpaceStale) {
                    staleCount += lockSpace.Entries.Count;

                    if (!check) {
                        lockSpace.Dimensions = space.Dimensions;
                        lockSpace.Model = space.Model;
                        lockSpace.Revision = space.Revision;
                        lockSpace.Entries.Clear();
                    }
                }

                // Check unused entries in this space
                foreach (var (hash, entry) in lockSpace.Entries.ToList()) {
                    if (!requiredTexts.Contains(value: entry.Text)) {
                        unusedCount++;
                        if (!check) {
                            lockSpace.Entries.Remove(key: hash);
                        }
                    }
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
                    var identity = new EmbeddingIdentity(
                        Dimensions: space.Dimensions,
                        Model: space.Model,
                        Revision: space.Revision
                    );

                    IEmbeddingGenerator<string, Embedding<float>> generator;

                    try {
                        generator = ResolveGenerator(
                            apiKeyEnv: apiKeyEnv,
                            apiKeyHeader: apiKeyHeader,
                            endpoint: endpoint,
                            identity: identity,
                            omitDimensions: omitDimensions,
                            provider: provider,
                            timeoutSeconds: timeoutSeconds
                        );
                    } catch (Exception ex) {
                        Console.Error.WriteLine(value: $"error: Failed to initialize provider for space '{space.Name}': {ex.Message}");
                        return 1;
                    }

                    using (generator) {
                        var boundedBatchSize = Math.Clamp(value: batchSize, min: 1, max: 2048);
                        var batches = EmbeddingBatcher.Batch(batchSize: boundedBatchSize, items: textsToEmbed);

                        foreach (var batch in batches) {
                            GeneratedEmbeddings<Embedding<float>> embeddings;

                            try {
                                embeddings = await generator.GenerateAsync(
                                    cancellationToken: cancellationToken,
                                    values: batch
                                ).ConfigureAwait(continueOnCapturedContext: false);
                            } catch (Exception ex) {
                                Console.Error.WriteLine(value: $"error: Embedding provider failed: {ex.Message}");
                                return 1;
                            }

                            var embeddingList = embeddings.ToList();

                            if (embeddingList.Count != batch.Count) {
                                Console.Error.WriteLine(value: $"error: Provider returned {embeddingList.Count} embeddings, expected {batch.Count}.");
                                return 1;
                            }

                            for (var i = 0; i < batch.Count; i++) {
                                var srcText = batch[i];
                                var emb = embeddingList[i];

                                if (!VectorQuantizer.TryQuantizeToBase64Url(embedding: emb, base64UrlVector: out var b64)) {
                                    Console.Error.WriteLine(value: $"error: Quantization failed for text \"{srcText}\".");
                                    return 1;
                                }

                                lockFile.SetEntry(identity: identity, spaceName: space.Name, text: srcText, vectorBase64Url: b64);
                            }
                        }
                    }
                }
            }

            if (check) {
                if (missingCount > 0 || staleCount > 0 || unusedCount > 0) {
                    Console.Error.WriteLine(
                        value: $"check failed for '{puckFile}': {missingCount} missing, {staleCount} stale, {unusedCount} unused."
                    );
                    overallExitCode = 1;
                } else {
                    Console.WriteLine(value: $"check passed: '{puckFile}' lock is up to date.");
                }
                continue;
            }

            var written = lockFile.Write(lockPath: lockPath);

            if (written) {
                Console.WriteLine(value: $"Updated lock file '{lockPath}'.");
            } else {
                Console.WriteLine(value: $"Lock file '{lockPath}' is up to date.");
            }
        }

        return overallExitCode;
    }

    private static async Task<int> ExecuteProbeAsync(
        string path,
        string text,
        string? space,
        string? against,
        int top,
        string? provider,
        string? endpoint,
        string? apiKeyEnv,
        string? apiKeyHeader,
        bool omitDimensions,
        int timeoutSeconds,
        CancellationToken cancellationToken
    ) {
        var fullPath = Path.GetFullPath(path: path);

        if (!File.Exists(path: fullPath)) {
            Console.Error.WriteLine(value: $"error: File '{path}' does not exist.");
            return 2;
        }

        var lockFile = EmbeddingLock.TryLoad(rootSourcePath: fullPath);

        if (lockFile is null || lockFile.Spaces.Count == 0) {
            Console.Error.WriteLine(value: $"error: No embedding lock file found for '{path}'. Run puck embed first.");
            return 1;
        }

        string resolvedSpaceName;

        if (!string.IsNullOrEmpty(value: space)) {
            resolvedSpaceName = space;
        } else if (lockFile.Spaces.Count == 1) {
            resolvedSpaceName = lockFile.Spaces.Keys.First();
        } else {
            Console.Error.WriteLine(value: "error: Multiple spaces declared in lock file; specify target space with --space.");
            return 1;
        }

        if (!lockFile.Spaces.TryGetValue(key: resolvedSpaceName, value: out var lockSpace)) {
            Console.Error.WriteLine(value: $"error: Space '{resolvedSpaceName}' not found in lock file.");
            return 1;
        }

        StateVector queryVector;

        if (lockFile.TryGet(spaceName: resolvedSpaceName, text: text, vectorBase64Url: out var existingVector)) {
            if (!StateVector.TryParseBase64Url(dimensions: lockSpace.Dimensions, error: out var err, text: existingVector, vector: out var vec)) {
                Console.Error.WriteLine(value: $"error: Corrupted lock vector: {err}");
                return 1;
            }
            queryVector = vec;
        } else {
            var identity = new EmbeddingIdentity(
                Dimensions: lockSpace.Dimensions,
                Model: lockSpace.Model,
                Revision: lockSpace.Revision
            );

            IEmbeddingGenerator<string, Embedding<float>> generator;

            try {
                generator = ResolveGenerator(
                    apiKeyEnv: apiKeyEnv,
                    apiKeyHeader: apiKeyHeader,
                    endpoint: endpoint,
                    identity: identity,
                    omitDimensions: omitDimensions,
                    provider: provider,
                    timeoutSeconds: timeoutSeconds
                );
            } catch (Exception ex) {
                Console.Error.WriteLine(value: $"error: Provider initialization failed: {ex.Message}");
                return 1;
            }

            using (generator) {
                var embs = await generator.GenerateAsync(
                    cancellationToken: cancellationToken,
                    values: [text]
                ).ConfigureAwait(continueOnCapturedContext: false);

                var embList = embs.ToList();

                if (embList.Count == 0 || !VectorQuantizer.TryQuantizeToBase64Url(base64UrlVector: out var b64, embedding: embList[0])) {
                    Console.Error.WriteLine(value: "error: Failed to quantize query vector.");
                    return 1;
                }

                if (!StateVector.TryParseBase64Url(dimensions: lockSpace.Dimensions, error: out var err, text: b64, vector: out var vec)) {
                    Console.Error.WriteLine(value: $"error: Failed to parse query vector: {err}");
                    return 1;
                }

                queryVector = vec;
            }
        }

        var results = new List<(string Label, long Similarity, long Dot)>();
        var querySpan = queryVector.Components;

        foreach (var entry in lockSpace.Entries.Values) {
            if (StateVector.TryParseBase64Url(dimensions: lockSpace.Dimensions, error: out _, text: entry.Vector, vector: out var targetVec)) {
                var targetSpan = targetVec.Components;
                var sim = SignedByteVectorFunctions.CosineQ16(left: querySpan, right: targetSpan);
                var dot = SignedByteVectorFunctions.Dot(left: querySpan, right: targetSpan);

                results.Add(item: (Label: entry.Text, Similarity: sim, Dot: dot));
            }
        }

        results.Sort(comparison: static (a, b) => b.Similarity.CompareTo(a.Similarity));

        var displayCount = Math.Min(val1: top, val2: results.Count);

        Console.WriteLine(value: $"Ranked similarity in space '{resolvedSpaceName}' (top {displayCount}):");

        for (var i = 0; i < displayCount; i++) {
            var (label, similarity, dot) = results[i];
            var simFloat = (similarity / 65536.0);
            Console.WriteLine(value: $"  {i + 1,2}. similarity: {simFloat:F4}  dot: {dot,6}  \"{label}\"");
        }

        return 0;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> ResolveGenerator(
        EmbeddingIdentity identity,
        string? provider,
        string? endpoint,
        string? apiKeyEnv,
        string? apiKeyHeader,
        bool omitDimensions,
        int timeoutSeconds
    ) {
        var isFixture = string.Equals(a: provider, b: "fixture", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a: identity.Model, b: FixtureEmbeddingGenerator.SupportedModel, comparisonType: StringComparison.OrdinalIgnoreCase);

        if (isFixture) {
            return new FixtureEmbeddingGenerator(identity: identity);
        }

        if (string.IsNullOrEmpty(value: endpoint)) {
            throw new InvalidOperationException(message: "Endpoint URL (--endpoint) must be specified for remote provider.");
        }

        var options = new OpenAiEmbeddingOptions {
            ApiKeyEnvironment = apiKeyEnv,
            ApiKeyHeader = (apiKeyHeader ?? "Authorization"),
            Dimensions = identity.Dimensions,
            Endpoint = new Uri(uriString: endpoint),
            Model = identity.Model,
            OmitDimensions = omitDimensions,
            Timeout = TimeSpan.FromSeconds(value: Math.Max(val1: 1, val2: timeoutSeconds)),
        };

        return OpenAiEmbeddingGeneratorFactory.Create(options: options);
    }

    private sealed record DiscoveredSpace(string Name, string Model, string Revision, int Dimensions);

    private static bool TryDiscoverSpacesAndTexts(
        string puckFilePath,
        out IReadOnlyList<DiscoveredSpace> spaces,
        out IReadOnlyDictionary<string, HashSet<string>> textsBySpace,
        [NotNullWhen(false)] out string? failure
    ) {
        spaces = [];
        textsBySpace = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
        failure = null;

        var sourceText = File.ReadAllText(path: puckFilePath);
        var diags = new DiagnosticBag();
        var vocabulary = CliVocabularyResolver.Instance.Resolve(source: sourceText);
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            diagnostics: diags,
            source: sourceText,
            vocabulary: vocabulary
        );

        if (diags.HasErrors || parseResult.Value is null) {
            failure = "Syntax errors in .puck source.";
            return false;
        }

        var document = parseResult.Value;
        var loweringDiags = new DiagnosticBag();
        var lowerResult = WorldDocumentEmitter.LowerWithDiagnostics(
            basePath: Path.GetDirectoryName(path: puckFilePath),
            diagnostics: loweringDiags,
            document: document,
            embeddings: null
        );

        var discoveredSpaces = new List<DiscoveredSpace>();

        if (lowerResult.Value is JsonObject root &&
            root["state"] is JsonObject stateObj &&
            stateObj["spaces"] is JsonArray spacesArr) {
            foreach (var spNode in spacesArr) {
                if (spNode is JsonObject spObj) {
                    var name = (spObj["name"]?.ToString() ?? "");
                    var model = (spObj["model"]?.ToString() ?? "");
                    var revision = (spObj["revision"]?.ToString() ?? "");
                    var dims = (spObj["dimensions"] is JsonValue dv && (dv.TryGetValue<int>(out var dVal) || (dv.TryGetValue<long>(out var lVal) && (dVal = (int)lVal) == dVal))) ? dVal : 0;

                    discoveredSpaces.Add(item: new DiscoveredSpace(Dimensions: dims, Model: model, Name: name, Revision: revision));
                }
            }
        }

        spaces = discoveredSpaces;

        var defaultSpace = (discoveredSpaces.Count == 1 ? discoveredSpaces[0].Name : null);
        var textsMap = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);

        foreach (var sp in discoveredSpaces) {
            textsMap[sp.Name] = new HashSet<string>(comparer: StringComparer.Ordinal);
        }

        // Collect all embedded texts from SQL blocks in the AST
        foreach (var stmt in document.Statements) {
            if (stmt is EmbeddedBlockNode { Language: var lang } embeddedBlock &&
                string.Equals(a: lang, b: "sql", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                var lexer = new StateSqlLexer(
                    baseColumn: embeddedBlock.BodyColumn,
                    baseLine: embeddedBlock.BodyLine,
                    baseOffset: embeddedBlock.BodyOffset,
                    source: embeddedBlock.Body
                );
                var tokens = lexer.Tokenize(diagnostics: new DiagnosticBag());
                var parser = new StateSqlParser(diagnostics: new DiagnosticBag(), tokens: tokens);
                var sqlStmts = parser.ParseStatements();

                foreach (var sqlStmt in sqlStmts) {
                    CollectSqlEmbedTexts(defaultSpace: defaultSpace, statement: sqlStmt, textsMap: textsMap);
                }
            }
        }

        textsBySpace = textsMap;
        return true;
    }

    private static void CollectSqlEmbedTexts(
        SqlStatement statement,
        string? defaultSpace,
        Dictionary<string, HashSet<string>> textsMap
    ) {
        var embedExprs = new List<SqlVectorLiteralExpression>();

        CollectEmbedExpressionsFromStatement(sink: embedExprs, statement: statement);

        foreach (var embed in embedExprs) {
            var targetSpace = (embed.Space ?? defaultSpace);

            if (!string.IsNullOrEmpty(value: targetSpace)) {
                if (!textsMap.TryGetValue(key: targetSpace, value: out var set)) {
                    set = new HashSet<string>(comparer: StringComparer.Ordinal);
                    textsMap[targetSpace] = set;
                }

                set.Add(item: embed.Payload);
            }
        }
    }

    private static void CollectEmbedExpressionsFromStatement(SqlStatement statement, List<SqlVectorLiteralExpression> sink) {
        switch (statement) {
            case SqlInsertStatement insert:
                foreach (var row in insert.ValuesRows) {
                    foreach (var expr in row) {
                        CollectEmbedExpressions(expr: expr, sink: sink);
                    }
                }
                break;

            case SqlUpdateStatement update:
                foreach (var assign in update.Assignments) {
                    CollectEmbedExpressions(expr: assign.Value, sink: sink);
                }
                if (update.Where is not null) {
                    CollectEmbedExpressions(expr: update.Where, sink: sink);
                }
                break;

            case SqlInsertSelectStatement insertSelect:
                foreach (var sel in insertSelect.SelectList) {
                    CollectEmbedExpressions(expr: sel, sink: sink);
                }
                if (insertSelect.Where is not null) {
                    CollectEmbedExpressions(expr: insertSelect.Where, sink: sink);
                }
                CollectEmbedExpressions(expr: insertSelect.OrderBy, sink: sink);
                break;

            case SqlDeclareSlotStatement slot:
                if (slot.DefaultValue is not null) {
                    CollectEmbedExpressions(expr: slot.DefaultValue, sink: sink);
                }
                break;

            case SqlCreateRuleStatement rule:
                foreach (var ruleStmt in rule.Statements) {
                    CollectEmbedExpressionsFromStatement(sink: sink, statement: ruleStmt);
                }
                break;
        }
    }

    private static void CollectEmbedExpressions(SqlExpression expr, List<SqlVectorLiteralExpression> sink) {
        switch (expr) {
            case SqlVectorLiteralExpression { Kind: "embed" } embed:
                sink.Add(item: embed);
                break;

            case SqlBinaryExpression bin:
                CollectEmbedExpressions(expr: bin.Left, sink: sink);
                CollectEmbedExpressions(expr: bin.Right, sink: sink);
                break;

            case SqlUnaryExpression un:
                CollectEmbedExpressions(expr: un.Operand, sink: sink);
                break;

            case SqlFunctionCallExpression fn:
                foreach (var arg in fn.Arguments) {
                    CollectEmbedExpressions(expr: arg, sink: sink);
                }
                break;
        }
    }
}
