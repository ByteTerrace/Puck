namespace Puck.Bench;

/// <summary>
/// The headless <c>--bench</c> handshake (plan §9) — the ONE cross-agent seam between the CLI parser and the demo's
/// composition root, so neither needs to reference the other's types. <c>Puck.Demo.Program</c> (the CLI) SETS these
/// properties, pre-host-build, from <c>--bench</c>/<c>--bench-samples</c>; the demo's bench installer READS them
/// after composition (once <see cref="BenchRuntime"/> and its scenes exist) and, when <see cref="Suite"/> is
/// non-null, submits the <c>bench.run</c> console line — subscribing <see cref="BenchRuntime.RunCompleted"/> to set
/// the process exit code and stop the host when <see cref="ExitWhenComplete"/>. A bare in-session <c>bench.run</c>
/// typed at the console never touches this class — it exists only for the headless CI/proof twin (§9 rule: no
/// restart, no separate product; the CLI flag is just process-boot sugar for the same console verb).
/// </summary>
public static class BenchBootRequest {
    /// <summary>The suite to run headless, or <see langword="null"/> when no <c>--bench</c> was requested (the
    /// default — the demo boots into the ordinary interactive session).</summary>
    public static string? Suite { get; set; }

    /// <summary>Whether the headless run should retain each scene's raw per-frame sample arrays in its JSON report
    /// (<c>--bench-samples</c>).</summary>
    public static bool IncludeSamples { get; set; }

    /// <summary>Whether the host should exit once the requested suite completes — set alongside <see cref="Suite"/>
    /// for every headless <c>--bench</c> invocation; a hypothetical future caller that wants the run to fire without
    /// tearing the process down would leave this <see langword="false"/>.</summary>
    public static bool ExitWhenComplete { get; set; }
}
