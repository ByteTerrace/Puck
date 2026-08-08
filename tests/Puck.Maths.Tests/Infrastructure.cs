using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Maths.Tests;

/// <summary>The four execution tiers. Plain <c>dotnet test</c> runs <see cref="Smoke"/> and <see cref="Default"/>;
/// <see cref="Deep"/> and <see cref="Bench"/> are excluded by the default runsettings and selected by their committed
/// per-tier <c>*.runsettings</c> (see the project README).</summary>
internal enum Tier {
    /// <summary>A handful of tiny facts, under two seconds — the fastest confidence check.</summary>
    Smoke,
    /// <summary>Everything but <see cref="Bench"/> and <see cref="Deep"/>; the plain-run set, under thirty seconds.</summary>
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
    /// <summary>Timing facts with breach-tolerant failure semantics; opt in with <c>bench.runsettings</c>.</summary>
    Bench,
}

/// <summary>Locates the committed test artifacts relative to this source file, so a test run writes the ledger,
/// frontier, manifest, and baselines back into the project directory rather than the build output.</summary>
internal static class TestPaths {
    /// <summary>Gets the absolute path of the test project directory (the directory holding this source file).</summary>
    public static string ProjectDirectory { get; } = ResolveProjectDirectory();

    /// <summary>Resolves the absolute path of a committed artifact by file name.</summary>
    /// <param name="fileName">The artifact file name, for example <c>frontier.json</c>.</param>
    /// <returns>The absolute path within the project directory.</returns>
    public static string Artifact(string fileName) =>
        Path.Combine(path1: ProjectDirectory, path2: fileName);

    private static string ResolveProjectDirectory([CallerFilePath] string callerFilePath = "") =>
        Path.GetDirectoryName(path: callerFilePath) ?? Directory.GetCurrentDirectory();
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

    /// <summary>Serializes <paramref name="value"/> to the canonical artifact form (indented, LF-terminated).</summary>
    /// <typeparam name="TValue">The model type; its declared property order is the on-disk order.</typeparam>
    /// <param name="value">The model to serialize.</param>
    /// <returns>The canonical JSON text.</returns>
    public static string Serialize<TValue>(TValue value) =>
        (JsonSerializer.Serialize(value: value, options: Options).ReplaceLineEndings(replacementText: "\n") + "\n");

    /// <summary>Reads and deserializes an artifact, or returns <see langword="default"/> when the file is absent.</summary>
    /// <typeparam name="TValue">The model type.</typeparam>
    /// <param name="path">The absolute artifact path.</param>
    /// <returns>The deserialized model, or <see langword="default"/> when the file does not exist.</returns>
    public static TValue? ReadOrDefault<TValue>(string path) =>
        (File.Exists(path: path)
            ? JsonSerializer.Deserialize<TValue>(json: File.ReadAllText(path: path), options: Options)
            : default);

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> only when it differs from the current
    /// file, comparing on LF-normalized text so line-ending drift never triggers a spurious write. The write is atomic:
    /// the content lands in a sibling temporary file that then replaces the target, so a crash mid-write can never leave
    /// a torn artifact — the reader sees either the old file or the whole new one.</summary>
    /// <param name="path">The absolute destination path.</param>
    /// <param name="content">The exact text to persist.</param>
    /// <returns><see langword="true"/> when the file was written; <see langword="false"/> when it was already current.</returns>
    public static bool WriteIfChanged(string path, string content) {
        var normalized = content.ReplaceLineEndings(replacementText: "\n");

        if (File.Exists(path: path) && (File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n") == normalized)) {
            return false;
        }

        // Same-directory temp so the replace is a rename within one volume (atomic), never a cross-volume copy; the
        // process id and a fresh guid keep concurrent writers from colliding on the staging name.
        var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        File.WriteAllText(path: temporaryPath, contents: normalized);
        File.Move(sourceFileName: temporaryPath, destFileName: path, overwrite: true);

        return true;
    }
}
