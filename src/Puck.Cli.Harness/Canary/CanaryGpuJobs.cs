using System.CommandLine;

namespace Puck.Cli.Canary;

/// <summary><c>--gpu-jobs</c>, the most canary legs on the GPU at once; <c>puck canary</c>, <c>puck affected</c> and
/// <c>puck gate</c> share it.</summary>
public static class CanaryGpuJobs {
    /// <summary>The legs a run keeps on the GPU at once unless <c>--gpu-jobs</c> says otherwise.</summary>
    public const int Default = 4;

    /// <summary>Creates <c>--gpu-jobs</c>.</summary>
    /// <returns>The option.</returns>
    public static Option<int> Create() => CliOptions.Count(
        defaultValue: Default,
        description: $"Maximum canary legs on the GPU at once: a windowed or offscreen World, or a leg requiring gpu (default {Default}). Each still holds its World processes of --jobs.",
        minimum: 1,
        name: "--gpu-jobs"
    );
}
