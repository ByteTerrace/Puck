using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Puck.Maths.Tests;

/// <summary>The four execution tiers. Plain <c>dotnet test</c> runs <see cref="Smoke"/> and <see cref="Default"/>;
/// <see cref="Deep"/> and <see cref="Exhaustive"/> are excluded by the default runsettings and selected by their
/// committed per-tier <c>*.runsettings</c> (see the project README). No tier measures time: cost is <c>puck bench</c>'s.</summary>
internal enum Tier {
    /// <summary>A handful of tiny facts, under two seconds — the fastest confidence check.</summary>
    Smoke,
    /// <summary>Everything but <see cref="Deep"/> and <see cref="Exhaustive"/>; the plain-run set, under thirty seconds.</summary>
    Default,
    /// <summary>Exhaustive edge batteries and larger sweeps, in minutes; the pre-commit guardian. Opt in with <c>deep.runsettings</c>.</summary>
    Deep,
    /// <summary>
    /// Full-width sweeps — every value of a carrier rather than a sample of one. Opt in with
    /// <c>exhaustive.runsettings</c>; expect tens of minutes and run it on demand or nightly, never in a change loop.
    /// </summary>
    /// <remarks>
    /// This is the only tier whose statements do NOT come from a <see cref="Domain"/>. A domain hands out an edge
    /// battery, a bounded random batch and a frontier block — a SAMPLE, by construction — which cannot express "every
    /// 32-bit value". Exhaustive cases therefore supply their own basis through <c>Laws.Claim</c>, and that is a
    /// requirement rather than a convention: a case here that consumed a domain would advance the frontier counter its
    /// Default sibling reads, sliding that sibling's operands as a side effect of a sweep having been run.
    /// </remarks>
    Exhaustive,
}
/// <summary>
/// Locates the committed ledger artifacts in the running checkout and the run's own ledger output. A run writes its
/// ledger into <see cref="RunDirectory"/> in the build output and never changes the checkout; <see cref="RecordCommand"/>
/// runs the suite and promotes that output over the committed artifacts.
/// </summary>
internal static class TestPaths {
    /// <summary>The <c>puck baselines</c> artifact name of the ledger, and the verb that records it.</summary>
    public const string RecordArtifact = "maths-ledger";
    /// <summary>The command that records the committed ledger artifacts from a fresh run.</summary>
    public const string RecordCommand = ("puck baselines " + RecordArtifact);

    /// <summary>The machine-written ledger artifacts, by file name.</summary>
    public static readonly string[] LedgerArtifacts = ["coverage-manifest.json", "frontier.json", "leg-ledger.md", "RESULTS.md"];
    /// <summary>Gets the absolute path of the test project directory (the directory holding this source file).</summary>
    public static string ProjectDirectory { get; } = RepositoryPaths.Resolve(relativePath: "tests/Puck.Maths.Tests");
    /// <summary>Gets the directory, beside the test assembly, that every run writes its ledger to.</summary>
    public static string RunDirectory { get; } = Puck.Testing.TestRecords.DirectoryOf(artifact: RecordArtifact);

    /// <summary>Resolves the absolute path of a committed artifact by file name.</summary>
    /// <param name="fileName">The artifact file name, for example <c>frontier.json</c>.</param>
    /// <returns>The absolute path within the project directory.</returns>
    public static string Artifact(string fileName) =>
        Path.Combine(
            path1: ProjectDirectory,
            path2: fileName
        );
    /// <summary>Resolves where this run writes a ledger artifact: its copy under <see cref="RunDirectory"/>.</summary>
    /// <param name="fileName">The artifact file name, for example <c>frontier.json</c>.</param>
    /// <returns>The absolute path this run writes.</returns>
    public static string Output(string fileName) =>
        Path.Combine(
            path1: RunDirectory,
            path2: fileName
        );
    /// <summary>Resolves where this run reads a ledger artifact's previous record from: its own output when an
    /// earlier run left one, otherwise the committed file.</summary>
    /// <param name="fileName">The artifact file name, for example <c>frontier.json</c>.</param>
    /// <returns>The absolute path of the previous record, which may not exist.</returns>
    public static string Previous(string fileName) =>
        (File.Exists(path: Output(fileName: fileName))
            ? Output(fileName: fileName)
            : Artifact(fileName: fileName)
        );
    /// <summary>Indicates whether a run may write <paramref name="path"/>: only under <see cref="RunDirectory"/>.</summary>
    /// <param name="path">The absolute path to write.</param>
    /// <returns><see langword="true"/> when the write keeps the checkout as the run found it.</returns>
    public static bool MayWrite(string path) =>
        Path.GetFullPath(path: path).StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: (Path.GetFullPath(path: RunDirectory) + Path.DirectorySeparatorChar)
        );
}
/// <summary>Deterministic JSON persistence for the committed artifacts: stable member and array ordering, LF line
/// endings, and update-on-change writes so an unchanged artifact never churns the working tree.</summary>
internal static class ArtifactJson {
    private static readonly JsonSerializerOptions Options = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Relaxed escaping keeps generic-member ids (angle brackets, ampersands) literal and readable in the committed
        // artifacts rather than as < escapes; the content stays deterministic.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    // Windows readers which omit FileShare.Delete (an editor, an indexer, a scanner) hold off an otherwise authorized
    // atomic rename for as long as they keep the file open, and nothing signals their close. Those native
    // sharing/access errors are retried until the operation succeeds or the run is cancelled, so how long a reader
    // holds on never decides the run. A persistent access denial reads the same as a held file and waits for
    // cancellation too; any other failure is thrown at once. The successful path never waits, and no fallback exposes
    // a partially rewritten artifact.
    private static void WithSharingRetries(Action operation) {
        var cancellationToken = TestContext.Current.CancellationToken;

        while (true) {
            try {
                operation();
                return;
            } catch (Exception exception) when (
                (OperatingSystem.IsWindows() &&
                (exception is IOException or UnauthorizedAccessException) &&
                ((exception.HResult & 0xFFFF) is 5 or 32 or 33))
            ) {
                if (cancellationToken.WaitHandle.WaitOne(millisecondsTimeout: 25)) {
                    throw new OperationCanceledException(
                        innerException: exception,
                        message: "the run was cancelled while a reader held an artifact open",
                        token: cancellationToken
                    );
                }
            }
        }
    }

    /// <summary>Reads and deserializes an artifact, or returns <see langword="default"/> when the file is absent.</summary>
    /// <typeparam name="TValue">The model type.</typeparam>
    /// <param name="path">The absolute artifact path.</param>
    /// <returns>The deserialized model, or <see langword="default"/> when the file does not exist.</returns>
    public static TValue? ReadOrDefault<TValue>(string path) =>
        (File.Exists(path: path)
            ? JsonSerializer.Deserialize<TValue>(
                json: File.ReadAllText(path: path),
                options: Options
            )
            : default
        );
    /// <summary>Serializes <paramref name="value"/> to the canonical artifact form (indented, LF-terminated).</summary>
    /// <typeparam name="TValue">The model type; its declared property order is the on-disk order.</typeparam>
    /// <param name="value">The model to serialize.</param>
    /// <returns>The canonical JSON text.</returns>
    public static string Serialize<TValue>(TValue value) =>
        (JsonSerializer.Serialize(
            options: Options,
            value: value
        ).ReplaceLineEndings(replacementText: "\n") + "\n");
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> only when it differs from the current
    /// file, comparing on LF-normalized text so line-ending drift never triggers a spurious write. The write is atomic:
    /// the content lands in a sibling temporary file that then replaces the target, so a crash mid-write can never leave
    /// a torn artifact — the reader sees either the old file or the whole new one.</summary>
    /// <param name="path">The absolute destination path; <see cref="TestPaths.MayWrite"/> must allow it. Its directory
    /// is created when missing.</param>
    /// <param name="content">The exact text to persist.</param>
    /// <returns><see langword="true"/> when the file was written; <see langword="false"/> when it was already current.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="path"/> lies outside the run's ledger directory.</exception>
    public static bool WriteIfChanged(string path, string content) {
        if (!TestPaths.MayWrite(path: path)) {
            throw new InvalidOperationException(message: $"{path} is committed; only {TestPaths.RecordCommand} writes it.");
        }

        var normalized = content.ReplaceLineEndings(replacementText: "\n");

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);

        if (
            File.Exists(path: path) &&
            (File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n") == normalized)
        ) {
            return false;
        }

        // Same-directory temp so the replace is a rename within one volume (atomic), never a cross-volume copy; the
        // process id and a fresh guid keep concurrent writers from colliding on the staging name.
        var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try {
            File.WriteAllText(
                contents: normalized,
                path: temporaryPath
            );
            WithSharingRetries(operation: () => File.Move(
                destFileName: path,
                overwrite: true,
                sourceFileName: temporaryPath
            ));
        } finally {
            // A failed replacement must not leave a staged artifact beside the source-controlled report.
            WithSharingRetries(operation: () => File.Delete(path: temporaryPath));
        }

        return true;
    }
}
