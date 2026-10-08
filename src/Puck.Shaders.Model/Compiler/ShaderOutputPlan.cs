namespace Puck.Shaders;

/// <summary>One output a compiler has planned (<see cref="ShaderCompiler.Plan"/>): a stage source compiled for one
/// target, with the closure it reads and the cache entry it compiles to.</summary>
public sealed class ShaderOutputPlan {
    internal ShaderOutputPlan(ShaderCompiler.StageWork work, ShaderTarget target, string inputs, string key, TimeSpan? recordedDuration) {
        Work = work;
        Target = target;
        Inputs = inputs;
        Key = key;
        RecordedDuration = recordedDuration;
    }

    /// <summary>Gets the UTF-8 bytes of the closure's distinct files, the stage source included.</summary>
    public long ClosureBytes => Work.Closure.ExpandedBytes;
    /// <summary>Gets every include the stage source reaches, by full path, in discovery order.</summary>
    public IReadOnlyList<ShaderSourceDependency> Includes => Work.Closure.Includes;
    /// <summary>Gets the hash of everything the output reads apart from the toolchain: the compiler revision, the step's
    /// tool and options, and every file of the closure by its path relative to the others and its content. A build's
    /// sidecar records it, so a check needs no toolchain.</summary>
    public string Inputs { get; }
    /// <summary>Gets the output's cache key: <see cref="Inputs"/> with the toolchain's identity.</summary>
    public string Key { get; }
    /// <summary>Gets how long this output's step last took on this machine, or <see langword="null"/> when no compile of
    /// a source of this name with these options has been measured.</summary>
    public TimeSpan? RecordedDuration { get; }
    /// <summary>Gets the stage source.</summary>
    public ShaderStageSource Stage => Work.Stage;
    /// <summary>Gets the target.</summary>
    public ShaderTarget Target { get; }

    internal ShaderCompiler.StageWork Work { get; }
}
/// <summary>One output a compiler produced for a plan (<see cref="ShaderCompiler.CompileOutputAsync"/>).</summary>
/// <param name="Bytecode">The bytecode, or <see langword="null"/> when the compile failed.</param>
/// <param name="Diagnostics">What the tool reported, mapped to the authored sources.</param>
/// <param name="Compiled">Whether a tool ran, rather than the cache answering.</param>
/// <param name="Elapsed">How long the answer took.</param>
public sealed record ShaderOutputResult(byte[]? Bytecode, IReadOnlyList<ShaderDiagnostic> Diagnostics, bool Compiled, TimeSpan Elapsed) {
    /// <summary>Gets whether the output has bytecode and no error.</summary>
    public bool IsSuccess => ((Bytecode is not null) && !Diagnostics.Any(predicate: static diagnostic => diagnostic.IsError));
}
