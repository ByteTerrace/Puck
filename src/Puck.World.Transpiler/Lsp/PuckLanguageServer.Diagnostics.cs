using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Transpiler.Ast;
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
// also marks every open document that reads it through a basis or an import, directly or through another open document,
// ahead of it in age, so the edited document is diagnosed first.
public sealed partial class PuckLanguageServer {
    private readonly Dictionary<string, int?> m_versions = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The mark each open document last received; a snapshot is current while the mark it carries is.
    private readonly Dictionary<string, long> m_generations = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The documents awaiting diagnosis, each with its mark; the highest mark is taken first.
    private readonly Dictionary<string, long> m_pending = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The files each open document read through a basis or an import at its last diagnosis that parsed.
    private readonly Dictionary<string, IReadOnlySet<string>> m_dependencies = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The semantic tier each document's current source tier left pending, under that source tier's mark.
    private readonly Dictionary<string, (long Mark, WorldSourceDiagnosis Tier)> m_semanticPending = new(comparer: StringComparer.OrdinalIgnoreCase);
    private PuckDiagnosticDepth m_depth = PuckDiagnosticDepth.Full;

    private long m_mark;
    private int m_sourceDiagnoses;
    private int m_semanticDiagnoses;

    private static int? ReadVersion(JsonObject document) => (((document["version"] is JsonValue value) && value.TryGetValue<int>(value: out var version))
        ? version
        : null
    );
    // The files a document names through its basis and its imports: a document name resolves beside the document to
    // both its source and its document file, and a module import is the path it spells.
    private static HashSet<string> ReadDependencies(DocumentNode document, string sourcePath) {
        var names = new List<string>();
        var dependencies = new HashSet<string>(comparer: PuckPaths.Comparer);

        if (document.Basis is { Length: > 0 } basis) {
            names.Add(item: basis);
        }
        foreach (var statement in document.Statements) {
            if (statement is ImportNode { Path.Length: > 0 } import) {
                names.Add(item: import.Path);
            }
        }
        foreach (var name in names) {
            if (WorldDefinitionFileSource.TryResolveDocumentBeside(
                documentPath: out var documentPath,
                name: name,
                reason: out _,
                referrerName: sourcePath,
                sourcePath: out var resolvedSource
            )) {
                _ = dependencies.Add(item: PuckPaths.Normalize(path: documentPath));
                _ = dependencies.Add(item: PuckPaths.Normalize(path: resolvedSource));
            }
            try {
                _ = dependencies.Add(item: PuckPaths.Normalize(path: Path.Combine(
                    path1: (Path.GetDirectoryName(path: sourcePath) ?? ""),
                    path2: name
                )));
            } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
                // A name no path can carry depends on nothing a file edit can change.
            }
        }

        return dependencies;
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

        // Farthest dependent first, so the nearest ones and then the edited document itself carry the newest marks.
        for (var index = (dependents.Count - 1); (index >= 0); --index) {
            Mark(uri: dependents[index]);
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
    /// <param name="Dependencies">The files the document read through its basis and imports, or
    /// <see langword="null"/> when it did not parse or this is a semantic-tier unit.</param>
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

        if (request.SourceTier is { } sourceTier) {
            _ = Interlocked.Increment(location: ref m_semanticDiagnoses);

            var semantic = WorldSourceDiagnostics.DiagnoseSemantic(
                cancellationToken: cancellationToken,
                catalogFingerprint: m_catalogFingerprint,
                diagnosis: sourceTier,
                machines: m_machines
            );

            cancellationToken.ThrowIfCancellationRequested();

            var union = new DiagnosticBag();

            union.AddRange(diagnostics: sourceTier.Diagnostics);
            union.AddRange(diagnostics: semantic);

            return new Diagnosis(
                Dependencies: null,
                Diagnostics: union,
                Request: request
            );
        }

        _ = Interlocked.Increment(location: ref m_sourceDiagnoses);

        var tier = WorldSourceDiagnostics.DiagnoseSource(
            cancellationToken: cancellationToken,
            foreign: m_diagnoseDocument,
            source: request.Text,
            sourcePath: request.SourcePath,
            vocabularies: m_vocabularyResolver
        );

        cancellationToken.ThrowIfCancellationRequested();

        return new Diagnosis(
            Dependencies: (((request.SourcePath is { } sourcePath) && (tier.Document is { } document))
                ? ReadDependencies(
                    document: document,
                    sourcePath: sourcePath
                )
                : null
            ),
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
            m_dependencies[diagnosis.Request.Uri] = dependencies;
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

    private void Publish(Diagnosis diagnosis) {
        var published = new JsonArray();

        foreach (var diagnostic in diagnosis.Diagnostics) {
            published.Add(item: new JsonObject {
                // A finding an import's declaration carries has a span into that import's text, not this one.
                ["range"] = LspJson.Range(
                    source: ((diagnostic.SourcePath is null) ? diagnosis.Request.Text : null),
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
            });
        }

        var @params = new JsonObject {
            ["uri"] = diagnosis.Request.Uri,
        };

        if (diagnosis.Request.Version is { } version) {
            @params["version"] = version;
        }
        @params["diagnostics"] = published;
        SendNotification(
            method: "textDocument/publishDiagnostics",
            @params: @params
        );
    }
}
