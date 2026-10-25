using System.Text;
using Puck.Assets;
using Puck.Scripting;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class AddonGuestLawTests {
    private sealed class HeldSource(CountdownEvent entered, ManualResetEventSlim release) : IAssetSource {
        public bool Exists(string path) => true;
        public ReadOnlyMemory<byte> Read(string path, int maximumBytes = int.MaxValue) {
            entered.Signal();
            if (!release.Wait(timeout: TimeSpan.FromSeconds(seconds: 30))) {
                throw new TimeoutException(message: "Compilation test did not release its admitted source load.");
            }
            return Encoding.UTF8.GetBytes(s: Quiet);
        }
    }

    [Fact]
    public async Task ConcurrentNativeCompilationIsBoundedAcrossLoadersAndFailuresReleaseCapacity() {
        using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        using var entered = new CountdownEvent(initialCount: AddonModuleLimits.ConcurrentCompilations);
        using var release = new ManualResetEventSlim();
        var tasks = Enumerable.Range(count: AddonModuleLimits.ConcurrentCompilations, start: 0).Select(selector: _ =>
            Task.Factory.StartNew(() => new WasmModuleLoader(engine, new HeldSource(entered: entered, release: release)).Load("held.wat"),
                TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try {
            Assert.True(condition: entered.Wait(TimeSpan.FromSeconds(seconds: 15), TestContext.Current.CancellationToken), userMessage: "Admitted loads did not reach their source.");
            Assert.Contains("compilation capacity", Assert.Throws<InvalidDataException>(testCode: () =>
                Load(engine, Encoding.UTF8.GetBytes(s: Quiet))).Message);
        } finally {
            release.Set();
            await Task.WhenAll(tasks);
        }
        // Every failure must relinquish its permit, even when native WAT parsing throws.
        for (var index = 0; (index <= AddonModuleLimits.ConcurrentCompilations); index++) {
            Assert.Throws<Wasmtime.WasmtimeException>(testCode: () => Load(engine, "not wat"u8.ToArray()));
        }
        using var control = Mount(engine, Quiet);

        Assert.Equal(AddonTickStatus.Ok, control.Tick(input: []).Status);
    }
}
