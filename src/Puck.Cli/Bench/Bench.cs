namespace Puck.Cli.Bench;

// Shared knobs for the microscope's benchmark classes.
internal static class Bench {
    // Inner dependent-chain length for every latency scenario. Set as OperationsPerInvoke so the framework reports
    // ns per single multiply (directly comparable to the gate's best-of-N ns/op), while the chain is long enough that
    // per-invocation call overhead is negligible and the JIT reaches steady state inside the loop.
    public const int LatencyOps = 4096;
}
