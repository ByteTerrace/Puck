using Wasmtime;

namespace Puck.Scripting;

/// <summary>
/// Owns the one configured <c>Wasmtime.Engine</c> every addon module compiles and instantiates
/// against. The engine is built from a locked deterministic <c>Config</c> — fuel counting on,
/// threads and SIMD off, NaN canonicalization on, Cranelift at a pinned optimization level — so a
/// module's fuel-halt point and numerics are reproducible across runs.
/// </summary>
public sealed class ScriptingEngine : IDisposable {
    private readonly Engine m_engine;

    /// <summary>Initializes the engine from the pinned deterministic options.</summary>
    /// <param name="options">The pinned configuration values, typically <see cref="ScriptingEngineOptions.Deterministic"/>.</param>
    public ScriptingEngine(ScriptingEngineOptions options) {
        m_engine = new Engine(config: BuildConfig(options: options));
    }

    /// <summary>Gets the shared configured engine addon modules compile and instantiate against.</summary>
    public Engine Engine => m_engine;

    /// <summary>Gets the resolved Wasmtime assembly version, which is the pinned native engine version, for gate assertions.</summary>
    public static string PinnedWasmtimeVersion => typeof(Wasmtime.Engine).Assembly.GetName().Version!.ToString();

    /// <summary>Disposes the underlying engine and its native resources.</summary>
    public void Dispose() {
        m_engine.Dispose();
    }

    private static Config BuildConfig(ScriptingEngineOptions options) {
        return new Config()
            .WithFuelConsumption(enable: true)
            .WithWasmThreads(enable: false)
            .WithSIMD(enable: false)
            .WithRelaxedSIMD(deterministic: false, enable: false)
            .WithCraneliftNaNCanonicalization(enable: true)
            .WithCompilerStrategy(strategy: CompilerStrategy.Cranelift)
            .WithOptimizationLevel(level: OptimizationLevel.Speed)
            .WithMaximumStackSize(size: options.MaxStackBytes);
    }
}
