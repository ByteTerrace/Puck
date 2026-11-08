namespace Puck.Shaders;

/// <summary>The shared runtime compile turnstile. Reloads and authored specializations compete for the same processor
/// budget; callers order a batch by its plans' recorded durations before requesting turns.</summary>
public static class ShaderCompileTurns {
    private static readonly SemaphoreSlim Turns = new(Environment.ProcessorCount, Environment.ProcessorCount);

    /// <summary>Compiles one plan while holding a shared turn, releasing it on every outcome.</summary>
    public static async Task<ShaderOutputResult> CompileAsync(ShaderCompiler compiler, ShaderOutputPlan plan, CancellationToken cancellationToken) {
        await Turns.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await compiler.CompileOutputAsync(plan, cancellationToken).ConfigureAwait(false); }
        finally { Turns.Release(); }
    }
}
