namespace Puck.AdvancedGamingBrick.Post;

// --hash-divergence <romA> [romB]: the per-tick hash-divergence localizer, dispatched out of TryRun to bound its
// cyclomatic complexity.
internal static partial class Diagnostics {
    /// <summary>
    /// Dispatches <c>--hash-divergence &lt;romA&gt; [romB]</c> — the per-tick hash-divergence localizer: step two
    /// machines in lockstep, snapshot-hashing each (FNV-1a) every frame (or every scanline with <c>--fine</c>) and
    /// comparing; on the first mismatch, full-state diff them to name the diverging component and byte offset.
    /// Self-check when <c>romB</c> is omitted (both machines boot <c>romA</c>; must never diverge). <c>--perturb-at
    /// &lt;frame&gt;</c> deliberately corrupts one EWRAM byte in machine B at that frame — a self-test of the tool,
    /// not of the core. <c>--frames &lt;n&gt;</c> sets how many frames to step (default 600). Kept out of
    /// <see cref="TryRun"/> to bound its cyclomatic complexity.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="exitCode">The exit code the mode produced, when handled.</param>
    /// <returns><see langword="true"/> when <c>--hash-divergence</c> was present (return <paramref name="exitCode"/>).</returns>
    private static bool TryHashDivergence(string[] args, out int exitCode) {
        exitCode = 0;

        var hashDivergenceIndex = Array.IndexOf(array: args, value: "--hash-divergence");

        if (hashDivergenceIndex < 0) {
            return false;
        }

        var romAPath = ((((hashDivergenceIndex + 1) < args.Length) && !args[(hashDivergenceIndex + 1)].StartsWith(value: "--", comparisonType: StringComparison.Ordinal))
            ? args[(hashDivergenceIndex + 1)]
            : null);

        if (romAPath is null) {
            Console.WriteLine(value: "  [SKIP] --hash-divergence: no ROM path supplied");
            exitCode = 2;

            return true;
        }

        var romBPath = ((((hashDivergenceIndex + 2) < args.Length) && !args[(hashDivergenceIndex + 2)].StartsWith(value: "--", comparisonType: StringComparison.Ordinal))
            ? args[(hashDivergenceIndex + 2)]
            : null);

        var fine = (Array.IndexOf(array: args, value: "--fine") >= 0);
        var framesArg = ArgValue(args: args, name: "--frames");
        var frames = ((framesArg is not null) ? int.Parse(s: framesArg) : 600);
        var perturbArg = ArgValue(args: args, name: "--perturb-at");
        var perturbAtFrame = ((perturbArg is not null) ? int.Parse(s: perturbArg) : (int?)null);

        exitCode = HashDivergenceProbe.Run(romAPath: romAPath, romBPath: romBPath, bios: BiosImage, frames: frames, fine: fine, perturbAtFrame: perturbAtFrame);

        return true;
    }
}
