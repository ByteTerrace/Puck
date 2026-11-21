using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Editing;
using Puck.World.Transpiler.Validation;

namespace Puck.World.Transpiler.Lsp;

/// <summary>How deep a language server session's diagnostics go.</summary>
public enum PuckDiagnosticDepth {
    /// <summary>The source tier alone: parse, import walk, lowering and lint. A host that gets the composed world's
    /// diagnostics another way (World Studio's world worker) asks for this, so its language worker never runs a
    /// composition.</summary>
    Source = 0,
    /// <summary>Both tiers: the source tier, then the composition, the engine's validation and the reference lint as
    /// a following unit of work.</summary>
    Full = 1,
}
// When diagnostics run. An edit records its text and marks the document pending; it never diagnoses, so a request that
// arrives right behind the edit that flushed it answers at once from the latest text. A host runs pending work only
// while its input is quiet, one unit at a time, most recently marked first, and the one core decides what is pending,
// what is stale and what is published:
//
// - `TakePendingDiagnosis` takes the next unit: a document's source tier, snapshotted at its latest text, or the
//   semantic tier its current source tier left pending;
// - `Diagnose` runs that unit and touches no server state, so a host may run it off its read loop and cancel it;
// - `CompleteDiagnosis` publishes the result only when the document has not been marked again since the snapshot, and
//   at full depth leaves the semantic tier pending behind a source tier that has one. That tier is a unit of its own,
//   so requests interleave between the two, and its publish carries both tiers' diagnostics.
//
// A document marked again while a unit runs is superseded: the host cancels it (`IsCurrent`), its result is dropped,
// any semantic tier it left pending is forgotten, and the newer mark is taken on the next quiet turn. Marking a document
// also marks every open document whose last diagnosis read it, directly or through another open document, ahead of it in
// age, so the edited document is diagnosed first. What a diagnosis read is the compile session's own record of it
// (CompileInputs): every library its import walk read, every document its composition read, at any depth.
public sealed partial class PuckLanguageServer {
    private readonly Dictionary<string, int?> m_versions = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The mark each open document last received; a snapshot is current while the mark it carries is.
    private readonly Dictionary<string, long> m_generations = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The documents awaiting diagnosis, each with its mark; the highest mark is taken first.
    private readonly Dictionary<string, long> m_pending = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The files each open document's last diagnosis read: its source tier's, joined by its semantic tier's.
    private readonly Dictionary<string, IReadOnlySet<string>> m_dependencies = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The semantic tier each document's current source tier left pending, under that source tier's mark.
    private readonly Dictionary<string, (long Mark, WorldSourceDiagnosis Tier)> m_semanticPending = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The last published compilation per open document URI, so completion and hover never compile synchronously on the message thread.
    private readonly Dictionary<string, WorldCompilation> m_lastCompilations = new(comparer: StringComparer.OrdinalIgnoreCase);
    // Per diagnosing-owner URI: the diagnostics it last contributed, grouped by target document URI.
    private readonly Dictionary<string, Dictionary<string, List<AttributedDiagnostic>>> m_publishedByOwner = new(comparer: StringComparer.OrdinalIgnoreCase);
    // Per target URI: the merged diagnostic list last published (used by code actions when context.diagnostics is empty).
    private readonly Dictionary<string, List<JsonObject>> m_publishedByTarget = new(comparer: StringComparer.OrdinalIgnoreCase);
    private PuckDiagnosticDepth m_depth = PuckDiagnosticDepth.Full;

    private sealed record AttributedDiagnostic(Diagnostic Diagnostic, JsonObject Wire);

    private long m_mark;
    private int m_sourceDiagnoses;
    private int m_semanticDiagnoses;

    private static int? ReadVersion(JsonObject document) => (((document["version"] is JsonValue value) && value.TryGetValue<int>(value: out var version))
        ? version
        : null
    );
    // The files a unit of diagnosis read, as the session recorded them: every file it read, found present or found
    // absent (an import or a basis that appears later is a read too). A listing or a directory's claims names a
    // directory, which no document is, and a sibling the name index only asked what it declares is not a file the
    // document composes, so all three are left out: an edit to that sibling reaches the document only when it saves and
    // moves the names, which the session's own facts see.
    private static HashSet<string> ReadFiles(CompileInputLog reads) {
        var files = new HashSet<string>(comparer: PuckPaths.Comparer);

        foreach (var input in reads.Inputs) {
            if (input.Kind is not (CompileInputKind.Listing or CompileInputKind.Declaration or CompileInputKind.Claims)) {
                _ = files.Add(item: PuckPaths.Normalize(path: input.Path));
            }
        }

        return files;
    }
    // An edit: the new text and version stand, and the document and every open document reading it are marked.
    private void Edited(string uri, string text, int? version) {
        m_documents[uri] = text;
        m_versions[uri] = version;

        var dependents = new List<string>();

        if (TryGetLocalPath(
            path: out var editedPath,
            uri: uri
        )) {
            var changed = new Queue<string>();
            var reached = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) { uri };

            changed.Enqueue(item: PuckPaths.Normalize(path: editedPath));

            while (changed.TryDequeue(result: out var path)) {
                foreach (var (dependent, reads) in m_dependencies) {
                    if (
                        reads.Contains(item: path) &&
                        m_documents.ContainsKey(key: dependent) &&
                        reached.Add(item: dependent)
                    ) {
                        dependents.Add(item: dependent);

                        if (TryGetLocalPath(
                            path: out var dependentPath,
                            uri: dependent
                        )) {
                            changed.Enqueue(item: PuckPaths.Normalize(path: dependentPath));
                        }
                    }
                }
            }
        }

        // A read set holds what a document read at every depth, so a dependent's distance from the edit is how many of
        // the other dependents it reads: a document reading none of them is nearest. Farthest dependent first, so the
        // nearest ones and then the edited document itself carry the newest marks.
        var paths = dependents.ToDictionary(
            elementSelector: dependent => (TryGetLocalPath(path: out var path, uri: dependent) ? PuckPaths.Normalize(path: path) : null),
            keySelector: static dependent => dependent,
            comparer: StringComparer.OrdinalIgnoreCase
        );

        // Among dependents equally far, the one reached first is marked last, as a breadth-first walk would mark it.
        foreach (var dependent in Enumerable.Reverse(source: dependents).OrderByDescending(keySelector: dependent => dependents.Count(predicate: other =>
            !ReferenceEquals(objA: other, objB: dependent) && (paths[other] is { } otherPath) && m_dependencies[dependent].Contains(item: otherPath)))) {
            Mark(uri: dependent);
        }
        Mark(uri: uri);
    }
    private void Mark(string uri) {
        var mark = ++m_mark;

        m_generations[uri] = mark;
        m_pending[uri] = mark;
        _ = m_semanticPending.Remove(key: uri);
    }

    /// <summary>One unit of diagnostic work: an open document's text, snapshotted, and the tier it is diagnosed
    /// to.</summary>
    /// <param name="Uri">The document's address.</param>
    /// <param name="Version">The version the client gave the text, or <see langword="null"/> when it gave none.</param>
    /// <param name="Text">The text.</param>
    /// <param name="SourcePath">The file the address names, or <see langword="null"/> for an address that names
    /// none.</param>
    /// <param name="Mark">The mark the document carried when it was snapshotted.</param>
    public sealed record DiagnosisRequest(string Uri, int? Version, string Text, string? SourcePath, long Mark) {
        /// <summary>Gets the source tier this unit completes with the semantic tier, or <see langword="null"/> for a
        /// source-tier unit.</summary>
        public WorldSourceDiagnosis? SourceTier { get; init; }
    }
    /// <summary>A finished unit of diagnostic work.</summary>
    /// <param name="Request">What was diagnosed.</param>
    /// <param name="Diagnostics">Everything the document is diagnosed with so far: the source tier's diagnostics, and
    /// the semantic tier's after them once that tier has run.</param>
    /// <param name="Dependencies">The files the unit read (a semantic-tier unit's join its source tier's), or
    /// <see langword="null"/> when the unit has no path to read beside.</param>
    public sealed record Diagnosis(DiagnosisRequest Request, DiagnosticBag Diagnostics, IReadOnlySet<string>? Dependencies) {
        /// <summary>Gets the source tier this unit ran, which a semantic-tier unit completes; <see langword="null"/>
        /// for a semantic-tier unit.</summary>
        public WorldSourceDiagnosis? SourceTier { get; init; }
    }

    /// <summary>Gets how deep the session's diagnostics go, as the client's <c>initialize</c> asked through
    /// <c>initializationOptions.diagnostics</c>: <see cref="PuckDiagnosticDepth.Full"/> unless it asked for
    /// <c>"source"</c>.</summary>
    public PuckDiagnosticDepth DiagnosticDepth => m_depth;
    /// <summary>Gets whether a unit of diagnostic work awaits the next quiet turn.</summary>
    public bool HasPendingDiagnostics => ((m_pending.Count > 0) || (m_semanticPending.Count > 0));
    /// <summary>Gets how many source-tier units have run — a load-independent count of the compiles diagnosis cost.</summary>
    public int SourceDiagnoses => Volatile.Read(location: ref m_sourceDiagnoses);
    /// <summary>Gets how many semantic-tier units have run — a load-independent count of the compositions diagnosis
    /// cost.</summary>
    public int SemanticDiagnoses => Volatile.Read(location: ref m_semanticDiagnoses);

    /// <summary>Takes the next unit of diagnostic work: the most recently marked document, whether its source tier or
    /// the semantic tier its current source tier left pending.</summary>
    /// <returns>The unit, or <see langword="null"/> when nothing awaits diagnosis.</returns>
    public DiagnosisRequest? TakePendingDiagnosis() {
        var source = ((m_pending.Count == 0)
            ? default(KeyValuePair<string, long>?)
            : m_pending.MaxBy(keySelector: static entry => entry.Value)
        );
        var semantic = ((m_semanticPending.Count == 0)
            ? default(KeyValuePair<string, (long Mark, WorldSourceDiagnosis Tier)>?)
            : m_semanticPending.MaxBy(keySelector: static entry => entry.Value.Mark)
        );

        if ((source is null) && (semantic is null)) {
            return null;
        }

        string uri;
        long mark;
        WorldSourceDiagnosis? tier = null;

        if ((semantic is { } next) && ((source is null) || (next.Value.Mark > source.Value.Value))) {
            (uri, mark, tier) = (next.Key, next.Value.Mark, next.Value.Tier);
            _ = m_semanticPending.Remove(key: uri);
        } else {
            (uri, mark) = (source!.Value.Key, source.Value.Value);
            _ = m_pending.Remove(key: uri);
        }

        return new DiagnosisRequest(
            Mark: mark,
            SourcePath: (TryGetLocalPath(
                path: out var sourcePath,
                uri: uri
            )
                ? sourcePath
                : null
            ),
            Text: m_documents[uri],
            Uri: uri,
            Version: m_versions.GetValueOrDefault(key: uri)
        ) { SourceTier = tier };
    }
    /// <summary>Gets whether <paramref name="request"/> still describes its document: it is open and has not been
    /// marked again since the snapshot. A host cancels a diagnosis whose request is no longer current.</summary>
    /// <param name="request">The snapshot.</param>
    /// <returns><see langword="true"/> while a result for it would still be published.</returns>
    public bool IsCurrent(DiagnosisRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);

        return (m_generations.TryGetValue(
            key: request.Uri,
            value: out var mark
        ) && (mark == request.Mark));
    }
    /// <summary>Runs one unit: the source tier of a snapshot (<see cref="WorldSourceDiagnostics.DiagnoseSource"/>:
    /// parse, import walk, lowering, lint), or the semantic tier that completes one
    /// (<see cref="WorldSourceDiagnostics.DiagnoseSemantic"/>: composition, the engine's validation, the reference
    /// lint). It reads no server state that an edit changes, so a host may run it while it handles other
    /// messages.</summary>
    /// <param name="request">The unit.</param>
    /// <param name="cancellationToken">Cancels the unit.</param>
    /// <returns>The diagnosis.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Diagnosis Diagnose(DiagnosisRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: request);

        var reads = new CompileInputLog();

        if (request.SourceTier is { } sourceTier) {
            _ = Interlocked.Increment(location: ref m_semanticDiagnoses);

            DiagnosticBag semantic;

            using (CompileInputs.Record(log: reads)) {
                semantic = WorldSourceDiagnostics.DiagnoseSemantic(
                    cancellationToken: cancellationToken,
                    catalogFingerprint: m_catalogFingerprint,
                    diagnosis: sourceTier,
                    machines: m_machines
                );
            }

            cancellationToken.ThrowIfCancellationRequested();

            var union = new DiagnosticBag();

            union.AddRange(diagnostics: sourceTier.Diagnostics);
            union.AddRange(diagnostics: semantic);

            return new Diagnosis(
                Dependencies: ((request.SourcePath is null) ? null : ReadFiles(reads: reads)),
                Diagnostics: union,
                Request: request
            );
        }

        _ = Interlocked.Increment(location: ref m_sourceDiagnoses);

        WorldSourceDiagnosis tier;

        using (CompileInputs.Record(log: reads)) {
            tier = WorldSourceDiagnostics.DiagnoseSource(
                cancellationToken: cancellationToken,
                foreign: m_diagnoseDocument,
                session: m_session,
                source: request.Text,
                sourcePath: request.SourcePath,
                vocabularies: m_vocabularyResolver
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new Diagnosis(
            Dependencies: ((request.SourcePath is null) ? null : ReadFiles(reads: reads)),
            Diagnostics: tier.Diagnostics,
            Request: request
        ) { SourceTier = tier };
    }
    /// <summary>Publishes <paramref name="diagnosis"/> to <paramref name="send"/> as a
    /// <c>textDocument/publishDiagnostics</c> carrying the diagnosed version, unless its request is no longer
    /// current, in which case nothing is sent. A source-tier unit at <see cref="PuckDiagnosticDepth.Full"/> depth whose
    /// document has a semantic tier leaves that tier pending, as the document's next unit.</summary>
    /// <param name="diagnosis">The finished diagnosis.</param>
    /// <param name="send">Receives the notification.</param>
    /// <returns><see langword="true"/> when the diagnosis was published.</returns>
    /// <exception cref="InvalidOperationException">The server is handling a message.</exception>
    public bool CompleteDiagnosis(Diagnosis diagnosis, Action<JsonObject> send) {
        ArgumentNullException.ThrowIfNull(argument: diagnosis);
        ArgumentNullException.ThrowIfNull(argument: send);

        if (!IsCurrent(request: diagnosis.Request)) {
            return false;
        }
        if (m_send is not null) {
            throw new InvalidOperationException(message: "The language server handles one message at a time.");
        }
        if (diagnosis.Dependencies is { } dependencies) {
            // A source tier replaces what the document read; the semantic tier that completes it adds what the
            // composition read.
            m_dependencies[diagnosis.Request.Uri] = (((diagnosis.SourceTier is null) && m_dependencies.TryGetValue(key: diagnosis.Request.Uri, value: out var sourceReads))
                ? new HashSet<string>(collection: sourceReads.Concat(second: dependencies), comparer: PuckPaths.Comparer)
                : dependencies);
        }
        if (diagnosis.SourceTier?.Compilation is { } compilation) {
            m_lastCompilations[diagnosis.Request.Uri] = compilation;
        }
        if (
            (diagnosis.SourceTier is { HasSemanticTier: true } tier) &&
            (m_depth == PuckDiagnosticDepth.Full)
        ) {
            m_semanticPending[diagnosis.Request.Uri] = (diagnosis.Request.Mark, tier);
        }

        m_send = send;

        try {
            Publish(diagnosis: diagnosis);
        } finally {
            m_send = null;
        }

        return true;
    }
    /// <summary>Runs one unit of pending diagnostic work to completion (<see cref="TakePendingDiagnosis"/>). A host whose
    /// input is quiet calls this; one that cannot interrupt a running call (the browser engine's single-threaded
    /// worker) calls it once per quiet turn, so requests interleave between units — including between a document's
    /// source tier and its semantic tier.</summary>
    /// <param name="send">Receives the <c>textDocument/publishDiagnostics</c> the unit publishes.</param>
    /// <param name="cancellationToken">Cancels the unit.</param>
    /// <returns><see langword="true"/> when a unit ran; <see langword="false"/> when nothing was pending.</returns>
    /// <remarks>A diagnosis that throws is answered with a <c>window/logMessage</c> naming the failure, as a message
    /// whose handling throws is.</remarks>
    public bool RunPendingDiagnosis(Action<JsonObject> send, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: send);

        if (TakePendingDiagnosis() is not { } request) {
            return false;
        }

        Diagnosis diagnosis;

        try {
            diagnosis = Diagnose(
                cancellationToken: cancellationToken,
                request: request
            );
        } catch (Exception exception) when ((exception is not OperationCanceledException)) {
            send(obj: LogNotification(message: $"LSP diagnosis error: {exception.Message}"));

            return true;
        }

        _ = CompleteDiagnosis(
            diagnosis: diagnosis,
            send: send
        );

        return true;
    }

    private string ToDocumentUri(string path) {
        var normalized = PuckPaths.Normalize(path: Path.GetFullPath(path: path));

        foreach (var openUri in m_documents.Keys) {
            if (
                TryGetLocalPath(path: out var openPath, uri: openUri) &&
                PuckPaths.Comparer.Equals(x: PuckPaths.Normalize(path: openPath), y: normalized)
            ) {
                return openUri;
            }
        }

        return new Uri(uriString: Path.GetFullPath(path: path)).AbsoluteUri;
    }

    private string? ReadSourceText(string? path) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            return null;
        }

        var normalized = PuckPaths.Normalize(path: Path.GetFullPath(path: path));

        foreach (var (openUri, openText) in m_documents) {
            if (
                TryGetLocalPath(path: out var openPath, uri: openUri) &&
                PuckPaths.Comparer.Equals(x: PuckPaths.Normalize(path: openPath), y: normalized)
            ) {
                return openText;
            }
        }

        try {
            var full = Path.GetFullPath(path: path);

            return (File.Exists(path: full) ? File.ReadAllText(path: full) : null);
        } catch {
            return null;
        }
    }

    private void Closed(string uri) {
        _ = m_documents.Remove(key: uri);
        _ = m_versions.Remove(key: uri);
        _ = m_generations.Remove(key: uri);
        _ = m_pending.Remove(key: uri);
        _ = m_dependencies.Remove(key: uri);
        _ = m_semanticPending.Remove(key: uri);
        _ = m_lastCompilations.Remove(key: uri);

        var affected = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) { uri };

        if (m_publishedByOwner.Remove(key: uri, value: out var previous)) {
            foreach (var targetUri in previous.Keys) {
                _ = affected.Add(item: targetUri);
            }
        }

        PublishMerged(affectedUris: affected, primaryUri: uri, primaryVersion: null);
    }

    private void Publish(Diagnosis diagnosis) {
        var ownerUri = diagnosis.Request.Uri;
        var ownerNormalized = ((diagnosis.Request.SourcePath is { } ownerPath)
            ? PuckPaths.Normalize(path: Path.GetFullPath(path: ownerPath))
            : null);
        var grouped = new Dictionary<string, List<AttributedDiagnostic>>(comparer: StringComparer.OrdinalIgnoreCase) {
            [ownerUri] = [],
        };
        var sourceCache = new Dictionary<string, string?>(comparer: StringComparer.OrdinalIgnoreCase) {
            [ownerUri] = diagnosis.Request.Text,
        };

        string? TextFor(string targetUri, string? sourcePath) {
            if (!sourceCache.TryGetValue(key: targetUri, value: out var text)) {
                text = (m_documents.TryGetValue(key: targetUri, value: out var openText)
                    ? openText
                    : ReadSourceText(path: sourcePath));
                sourceCache[targetUri] = text;
            }
            return text;
        }

        foreach (var diagnostic in diagnosis.Diagnostics) {
            var isForeign = (
                (diagnostic.SourcePath is { Length: > 0 } foreignPath) &&
                ((ownerNormalized is null) || !PuckPaths.Comparer.Equals(x: PuckPaths.Normalize(path: Path.GetFullPath(path: foreignPath)), y: ownerNormalized))
            );
            var targetUri = (isForeign ? ToDocumentUri(path: diagnostic.SourcePath!) : ownerUri);
            var rangeSource = TextFor(sourcePath: diagnostic.SourcePath, targetUri: targetUri);
            var wire = new JsonObject {
                ["range"] = LspJson.Range(
                    source: rangeSource,
                    span: diagnostic.Span
                ),
                ["severity"] = diagnostic.Severity switch {
                    DiagnosticSeverity.Error => 1,
                    DiagnosticSeverity.Warning => 2,
                    _ => 3,
                },
                ["code"] = diagnostic.Code,
                ["source"] = "puck",
                ["message"] = diagnostic.Message,
            };

            if (isForeign || (diagnostic.Related is not null)) {
                var relatedInfo = new JsonArray();

                if (diagnostic.Related is { } related) {
                    var relatedUri = ((related.SourcePath is { Length: > 0 } relPath)
                        ? ToDocumentUri(path: relPath)
                        : ownerUri);
                    var relatedText = TextFor(sourcePath: related.SourcePath, targetUri: relatedUri);

                    relatedInfo.Add(item: new JsonObject {
                        ["location"] = new JsonObject {
                            ["uri"] = relatedUri,
                            ["range"] = LspJson.Range(source: relatedText, span: related.Span),
                        },
                        ["message"] = related.Message,
                    });
                } else {
                    var useSpan = SourceSpan.None;

                    if (
                        (m_lastCompilations.TryGetValue(key: ownerUri, value: out var lastComp)) &&
                        (lastComp.SourceMap.TryGetSpan(jsonPointer: "/basis", span: out var basisSpan) || lastComp.SourceMap.TryGetSpan(jsonPointer: "/imports/0", span: out basisSpan))
                    ) {
                        useSpan = basisSpan;
                    }

                    var ownerLabel = ((diagnosis.Request.SourcePath is { Length: > 0 } path)
                        ? Path.GetFileName(path: path)
                        : ownerUri);

                    relatedInfo.Add(item: new JsonObject {
                        ["location"] = new JsonObject {
                            ["uri"] = ownerUri,
                            ["range"] = LspJson.Range(source: diagnosis.Request.Text, span: useSpan),
                        },
                        ["message"] = $"Reported while diagnosing '{ownerLabel}'.",
                    });
                }

                wire["relatedInformation"] = relatedInfo;
            }

            if (!grouped.TryGetValue(key: targetUri, value: out var bucket)) {
                bucket = [];
                grouped[targetUri] = bucket;
            }
            bucket.Add(item: new AttributedDiagnostic(Diagnostic: diagnostic, Wire: wire));
        }

        var affected = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) { ownerUri };

        if (m_publishedByOwner.TryGetValue(key: ownerUri, value: out var previous)) {
            foreach (var previousUri in previous.Keys) {
                _ = affected.Add(item: previousUri);
            }
        }
        foreach (var targetUri in grouped.Keys) {
            _ = affected.Add(item: targetUri);
        }

        m_publishedByOwner[ownerUri] = grouped;
        PublishMerged(affectedUris: affected, primaryUri: ownerUri, primaryVersion: diagnosis.Request.Version);
    }

    private void PublishMerged(HashSet<string> affectedUris, string primaryUri, int? primaryVersion) {
        // Publish the primary (diagnosed) document first, then any foreign target URIs in deterministic order.
        var ordered = affectedUris
            .OrderByDescending(keySelector: uri => StringComparer.OrdinalIgnoreCase.Equals(x: uri, y: primaryUri))
            .ThenBy(keySelector: static uri => uri, comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var targetUri in ordered) {
            var merged = new JsonArray();
            var snapshot = new List<JsonObject>();
            var seen = new HashSet<Diagnostic>();

            foreach (var ownerGroups in m_publishedByOwner.Values) {
                if (!ownerGroups.TryGetValue(key: targetUri, value: out var entries)) {
                    continue;
                }
                foreach (var entry in entries) {
                    if (seen.Add(item: entry.Diagnostic)) {
                        var clone = ((JsonObject)entry.Wire.DeepClone());

                        merged.Add(item: clone);
                        snapshot.Add(item: ((JsonObject)entry.Wire.DeepClone()));
                    }
                }
            }

            if (snapshot.Count > 0) {
                m_publishedByTarget[targetUri] = snapshot;
            } else {
                _ = m_publishedByTarget.Remove(key: targetUri);
            }

            var @params = new JsonObject {
                ["uri"] = targetUri,
            };

            if (StringComparer.OrdinalIgnoreCase.Equals(x: targetUri, y: primaryUri) && (primaryVersion is { } pVersion)) {
                @params["version"] = pVersion;
            } else if (m_versions.TryGetValue(key: targetUri, value: out var openVersion) && (openVersion is { } oVersion)) {
                @params["version"] = oVersion;
            }
            @params["diagnostics"] = merged;
            SendNotification(
                method: "textDocument/publishDiagnostics",
                @params: @params
            );
        }
    }
}
