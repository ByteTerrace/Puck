using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.World.Machines;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves that neutral content preparation and runtime registration belong to the selected host.</summary>
public sealed class MachineExtensionCatalogLawTests {
    [Fact]
    public void ADeviceWithoutPresentationInputOrRemovableContentStillAdvances() {
        var path = Path.GetTempFileName();
        try {
            var engine = new TinyEngine("tiny");
            using var host = new WorldMachineHost([Screen("tiny", path)], new WorldMachineCatalog([engine]));
            var runtime = Assert.IsType<TinyMachine>(host.MachineAt(0));

            host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
            host.Advance(420, ReadOnlyMemory<ScreenPadSnapshot>.Empty);

            Assert.Equal(1260UL, runtime.ElapsedTicks);
            Assert.Equal(2, host.State(0)!.Value.FramesStepped);
            Assert.Null(host.VideoOutput(0));
            Assert.Null(host.AudioMachine(0));
            Assert.Equal((nint)0, host.Handle(0));
            Assert.Equal(Vector3.Zero, host.Light(0));
            Assert.False((IMachineRuntime)runtime is IMachineContentSlot);
            Assert.False((IMachineRuntime)runtime is IMachineInputPorts);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void IndependentHostsPrepareTheSameFormatThroughTheirOwnProviders() {
        var path = Path.Combine(Path.GetTempPath(), $"puck-machine-{Guid.NewGuid():N}.tiny");
        File.WriteAllBytes(path, [17]);
        try {
            var firstEngine = new TinyEngine("tiny");
            var secondEngine = new TinyEngine("tiny");
            var rawEngine = new TinyEngine("tiny");
            var firstProvider = new TinyContentProvider("tiny", 41);
            var secondProvider = new TinyContentProvider("tiny", 73);
            var screen = Screen("tiny", path);
            using var first = new WorldMachineHost([screen], new WorldMachineCatalog([firstEngine], [firstProvider]));
            using var second = new WorldMachineHost([screen], new WorldMachineCatalog([secondEngine], [secondProvider]));
            using var raw = new WorldMachineHost([screen], new WorldMachineCatalog([rawEngine]));

            Assert.Equal(new byte[] { 41 }, firstEngine.LoadedImage);
            Assert.Equal(new byte[] { 73 }, secondEngine.LoadedImage);
            Assert.Equal(new byte[] { 17 }, rawEngine.LoadedImage);
            Assert.True(first.TryResolveSymbol(0, "counter", out var firstAddress));
            Assert.True(second.TryResolveSymbol(0, "counter", out var secondAddress));
            Assert.Equal(41, firstAddress);
            Assert.Equal(73, secondAddress);
            Assert.False(raw.TryResolveSymbol(0, "counter", out _));
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnEmptyHostCannotPrepareAnEngineFromAnotherCatalog() {
        var candidate = Fixtures.BuildDocument() with { ScreensRaw = [Screen("gaming-brick", "")] };
        using var empty = new WorldMachineHost([], new WorldMachineCatalog([]));
        using var installed = new WorldMachineHost([], TestHookInstaller.CreateMachineCatalog());

        Assert.False(empty.TryPrepare(null, candidate, out var refused, out var reason));
        Assert.Null(refused);
        Assert.Contains("gaming-brick", reason, StringComparison.Ordinal);
        Assert.True(installed.TryPrepare(null, candidate, out var admitted, out _));
        admitted!.Dispose();
    }

    [Fact]
    public void OfflineChecksReportDeferralAndAdmissionUsesOnlyTheSuppliedCatalog() {
        var candidate = Fixtures.BuildDocument() with { ScreensRaw = [Screen("gaming-brick", "")] };
        var errors = new List<string>();
        var deferred = new List<string>();
        Assert.True(WorldDefinitionValidator.TryValidateLocally(candidate, machines: null, errors, deferred, out _));
        Assert.Empty(errors);
        Assert.Contains("no machine catalog", Assert.Single(deferred), StringComparison.Ordinal);

        Assert.False(WorldDefinitionValidator.TryValidateLocally(candidate, new WorldMachineCatalog([]), out var reason));
        Assert.Contains("gaming-brick", reason, StringComparison.Ordinal);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(candidate, TestHookInstaller.CreateMachineCatalog(), out reason), reason);
    }

    [Fact]
    public void RegistrationSnapshotsAreIndependentAndDuplicateIdsRefuse() {
        var registry = new WorldMachineExtensionRegistry();
        var engine = new TinyEngine("first");
        registry.RegisterEngine(engine);
        var before = registry.Build();
        registry.RegisterEngine(new TinyEngine("second"));
        var after = registry.Build();

        Assert.False(before.IsRegistered("second"));
        Assert.True(after.IsRegistered("second"));
        Assert.Throws<ArgumentException>(() => registry.RegisterEngine(new TinyEngine("first")));
        Assert.Same(engine, registry.Build().Engines["first"]);
    }

    [Fact]
    public void AProviderForAnotherEngineRefusesWithoutRegisteringEitherHalf() {
        var registry = new WorldMachineExtensionRegistry();
        Assert.Throws<ArgumentException>(() => registry.RegisterEngine(new TinyEngine("first"), new TinyContentProvider("second", 1)));
        Assert.Empty(registry.Build().Engines);
        Assert.Empty(registry.Build().ContentProviders);
    }

    [Fact]
    public void AContentProviderWithoutAnEngineCannotBecomeAHostCatalog() {
        var registry = new WorldMachineExtensionRegistry();
        registry.RegisterContentProvider(new TinyContentProvider("missing", 1));
        Assert.Throws<ArgumentException>(() => registry.Build());
    }

    private static WorldScreen Screen(string engine, string path) => new(
        Index: 0, Origin: Vector3.Zero, Right: Vector3.UnitX, Up: Vector3.UnitY,
        HalfWidth: 1, HalfHeight: 1, HalfDepth: 0.01f, Round: 0,
        Source: new WorldScreenSource.Machine(engine, path, null), Route: WorldScreenRoute.Passive);

    private sealed class TinyContentProvider(string engineId, byte value) : IMachineContentProvider {
        public string EngineId => engineId;
        public bool Recognizes(string contentPath) => contentPath.EndsWith(".tiny", StringComparison.Ordinal);
        public PreparedMachineContent Prepare(ReadOnlyMemory<byte> content) => new(
            Image: [value], SourceHash: WorldDefinitionFileSource.ComputeContentHash(content.Span),
            Symbols: new Dictionary<string, MachineContentSymbol> { ["counter"] = new("bus", value) });
    }

    private sealed class TinyEngine(string id) : IMachineEngine {
        public string Id => id;
        public MachineEngineDescriptor Descriptor { get; } = new(id, "Test runtime", new("puck.tiny.config.v1", []), [], [], [], [], []);
        public byte[]? LoadedImage { get; private set; }
        public IMachineRuntime CreateMachine(MachineCreationRequest request) => new TinyMachine();
        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
            LoadedImage = contentBytes;
            return new TinyMachine();
        }
    }

    private sealed class TinyMachine : IMachineRuntime {
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;
        public ulong ElapsedTicks { get; private set; }
        public bool Advance(ulong deltaTicks) {
            ElapsedTicks += deltaTicks;
            return deltaTicks != 0;
        }
        public void Dispose() { }
    }
}
