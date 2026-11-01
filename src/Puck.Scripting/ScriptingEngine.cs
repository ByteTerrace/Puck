using Wasmtime;

namespace Puck.Scripting;

/// <summary>
/// Owns the one configured <c>Wasmtime.Engine</c> every addon module compiles and instantiates
/// against. The engine is built from a locked deterministic <c>Config</c> — fuel counting on,
/// threads and SIMD off, NaN canonicalization on, Cranelift at a pinned optimization level — so a
/// module's fuel-halt point and numerics are reproducible across runs.
/// Only one 32-bit linear memory is admitted; GC heaps and typed function references are disabled.
/// </summary>
public sealed class ScriptingEngine : IDisposable {
    private readonly Engine m_engine;
    private readonly Timer m_epochTimer;
    private readonly Lock m_epochGate = new();

    private bool m_disposed;

    /// <summary>Each guest call gets at most ten host timer epochs (approximately 100 ms) in addition to fuel.</summary>
    public const ulong CallDeadlineEpochs = 10;

    /// <summary>Initializes the engine from the pinned deterministic options.</summary>
    /// <param name="options">The pinned configuration values, typically <see cref="ScriptingEngineOptions.Deterministic"/>.</param>
    public ScriptingEngine(ScriptingEngineOptions options) {
        m_engine = new Engine(config: BuildConfig(options: options));
        m_epochTimer = new Timer(_ => {
            lock (m_epochGate) { if (!m_disposed) { m_engine.IncrementEpoch(); } }
        }, null, TimeSpan.FromMilliseconds(milliseconds: 10), TimeSpan.FromMilliseconds(milliseconds: 10));
    }

    /// <summary>Gets the shared configured engine addon modules compile and instantiate against.</summary>
    public Engine Engine => m_engine;
    /// <summary>Gets the resolved Wasmtime assembly version, which is the pinned native engine version, for gate assertions.</summary>
    public static string PinnedWasmtimeVersion => typeof(Wasmtime.Engine).Assembly.GetName().Version!.ToString();

    private static Config BuildConfig(ScriptingEngineOptions options) {
        return WasmtimeSignals.WithoutSignalHandlers(config: new Config())
            .WithFuelConsumption(enable: true)
            .WithEpochInterruption(enable: true)
            .WithWasmThreads(enable: false)
            .WithMultiMemory(enable: false)
            .WithMemory64(enable: false)
            .WithGc(enable: false)
            .WithFunctionReferences(enable: false)
            .WithSIMD(enable: false)
            .WithRelaxedSIMD(
            deterministic: false,
            enable: false
        )
            .WithCraneliftNaNCanonicalization(enable: true)
            .WithCompilerStrategy(strategy: CompilerStrategy.Cranelift)
            .WithOptimizationLevel(level: OptimizationLevel.Speed)
            .WithMaximumStackSize(size: options.MaxStackBytes);
    }

    /// <summary>Disposes the underlying engine and its native resources.</summary>
    public void Dispose() {
        lock (m_epochGate) {
            if (m_disposed) { return; }
            m_disposed = true;
            m_epochTimer.Dispose();
            m_engine.Dispose();
        }
    }
}
