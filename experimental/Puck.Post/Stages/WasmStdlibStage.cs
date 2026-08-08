using Puck.Scripting;

namespace Puck.Post;

/// <summary>
/// Tier-A stage. The WASM standard library drift gate: every artifact registered in
/// <see cref="WasmStdlibSources.All"/> — <c>wasm/puck-stdlib/src/fixed_generated.rs</c> (the ported
/// functions, tables, and polynomial coefficients) and its sibling <c>fixed_vectors.rs</c> (known-answer
/// vectors computed by calling the real host at generation time), plus
/// <c>wasm/puck-stdlib/src/abi_generated.rs</c> (the <c>CommandPhase</c>/<c>AddonButtons</c>/<c>AddonAbi</c>
/// Rust mirror) — is a committed artifact, generated from the live host types by
/// <see cref="Puck.Maths.FixedQ4816RustPort"/> and <see cref="AddonAbiRustPort"/>. This stage calls each
/// registered emitter in-process and compares its output, byte for byte (line-ending normalized — the
/// repository has mixed CRLF/LF), against what is checked in — the same pattern
/// <see cref="RunDocumentStage"/> uses for <c>schema/run.schema.json</c>. This proves the committed
/// artifacts are CURRENT with the live host and nothing more: it does NOT prove the ported/mirrored Rust
/// is itself correct. For the FixedQ4816 port that proof is <c>cargo test --target &lt;host-triple&gt;</c>
/// run from the crate, where every vector in <c>fixed_vectors.rs</c> is asserted against the port; POST
/// deliberately does not invoke a Rust toolchain, so the two halves are complementary — this gate keeps
/// the vectors honest, and <c>cargo test</c> keeps the port honest against them. Iterating the registry
/// rather than naming the files by hand means a future artifact needs no change here — only a one-line
/// addition to <see cref="WasmStdlibSources.All"/>.
/// </summary>
internal sealed class WasmStdlibStage : IPostStage {
    private const string RegenerateCommand = "dotnet run --project src/Puck.Cli -c Release -- wasm-stdlib";

    /// <inheritdoc/>
    public string Name => "wasm-stdlib";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var repositoryRoot = FindRepositoryRoot();

        if (repositoryRoot is null) {
            return PostStageOutcome.Fail(detail: "could not locate the repository root (a directory containing docs\\examples and schema\\run.schema.json) above the base or current directory");
        }

        foreach (var source in WasmStdlibSources.All) {
            var fullPath = Path.Combine(path1: repositoryRoot, path2: source.RelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));

            if (!File.Exists(path: fullPath)) {
                return PostStageOutcome.Fail(detail: $"{source.RelativePath} does not exist; generate it: {RegenerateCommand}");
            }

            var live = source.Emit().ReplaceLineEndings(replacementText: "\n").TrimEnd(trimChar: '\n');
            var committed = File.ReadAllText(path: fullPath).ReplaceLineEndings(replacementText: "\n").TrimEnd(trimChar: '\n');

            if (!string.Equals(a: committed, b: live, comparisonType: StringComparison.Ordinal)) {
                return PostStageOutcome.Fail(detail: $"{source.RelativePath} has drifted from the live host; regenerate it: {RegenerateCommand}");
            }
        }

        // The count of artifacts verified is the only thing this stage legitimately knows. It deliberately
        // reports NO statistic derived from an artifact's contents — no byte counts (the compared lengths are
        // line-ending normalized, so they disagree with the raw figures the `wasm-stdlib` verb prints for the
        // same files) and no vector tally. Sniffing a tally out of the text would require this generic stage
        // to recognize one artifact's interior format, which it cannot do safely: a tuple-shaped line counter
        // also matches ordinary Rust such as `(1i64 << 62).wrapping_add(...)` in the ported source, so the
        // number would be quietly wrong and would rot the moment a new artifact joins the registry. How far
        // the known-answer proof reaches is `cargo test`'s to report, where it is measured rather than guessed.
        return PostStageOutcome.Pass(detail: $"{WasmStdlibSources.All.Count} generated artifacts in sync with the live host");
    }

    // Walks up from the app base and the working directory to the checkout root — the POST is a dev-box tool that runs
    // from the repository (dotnet run), so both anchors normally resolve; failing to find it is a loud stage fail, not
    // a skip. Mirrors RunDocumentStage.FindRepositoryRoot exactly (same anchors, same walk).
    private static string? FindRepositoryRoot() {
        foreach (var anchor in (string?[])[AppContext.BaseDirectory, Environment.CurrentDirectory]) {
            for (var directory = anchor; (directory is not null); directory = Path.GetDirectoryName(path: directory)) {
                if (Directory.Exists(path: Path.Combine(path1: directory, path2: "docs", path3: "examples")) && File.Exists(path: Path.Combine(path1: directory, path2: "schema", path3: "run.schema.json"))) {
                    return directory;
                }
            }
        }

        return null;
    }
}
