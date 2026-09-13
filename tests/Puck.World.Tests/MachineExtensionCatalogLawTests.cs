using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.World.Machines;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves that neutral content preparation and runtime registration belong to the selected host.</summary>
public sealed class MachineExtensionCatalogLawTests {
    private static WorldMachine Machine(string engine, string path) => new(
        "tiny",
        engine,
        JsonSerializer.SerializeToElement(new { schema = "puck.tiny.config.v1", content = new { path } })
    );

    [Fact]
    public void AContentProviderWithoutAnEngineCannotBecomeAHostCatalog() {
        var registry = new WorldMachineExtensionRegistry();

        registry.RegisterContentProvider(contentProvider: new TinyContentProvider(
            engineId: "missing",
            value: 1
        ));
        Assert.Throws<ArgumentException>(testCode: () => registry.Build());
    }
    [Fact]
    public void ADeviceWithoutPresentationInputOrRemovableContentStillAdvances() {
        var path = Path.GetTempFileName();

        File.WriteAllBytes(
            bytes: [1],
            path: path
        );
        try {
            var engine = new TinyEngine(id: "tiny");
            using var host = new WorldMachineHost(
                [],
                new WorldMachineCatalog(
                    [engine],
                    [new TinyContentProvider(
                            engineId: "tiny",
                            value: 41
                        )]
                )
            );
            var definition = Fixtures.BuildDocument() with {
                ScreensRaw = null,
                MachinesRaw = [new WorldMachine(
                    "tiny",
                    "tiny",
                    JsonSerializer.SerializeToElement(new { schema = "puck.tiny.config.v1", content = new { path } })
                )],
            };

            Assert.True(
                condition: host.TryPrepare(
                    candidate: definition,
                    current: null,
                    plan: out var plan,
                    reason: out var reason
                ),
                userMessage: reason
            );
            using (plan) { host.Commit(plan: plan!); host.Finish(plan: plan!); }
            var runtime = Assert.IsType<TinyMachine>(@object: engine.Created.Single());

            host.Advance(
                840,
                ReadOnlyMemory<ScreenPadSnapshot>.Empty
            );
            host.Advance(
                420,
                ReadOnlyMemory<ScreenPadSnapshot>.Empty
            );

            Assert.Equal(
                1260UL,
                runtime.ElapsedTicks
            );
            Assert.Equal(
                2,
                host.InstanceState(name: "tiny")!.Value.FramesStepped
            );
            Assert.Null(@object: host.VideoOutput(
                instance: "tiny",
                output: "video"
            ));
            Assert.Null(@object: host.AudioOutput(
                instance: "tiny",
                output: "audio"
            ));
            Assert.False(condition: (((IMachineRuntime)runtime) is IMachineContentSlot));
            Assert.False(condition: (((IMachineRuntime)runtime) is IMachineInputPorts));
        } finally {
            File.Delete(path: path);
        }
    }
    [Fact]
    public void AProviderForAnotherEngineRefusesWithoutRegisteringEitherHalf() {
        var registry = new WorldMachineExtensionRegistry();

        Assert.Throws<ArgumentException>(testCode: () => registry.RegisterEngine(
            new TinyEngine(id: "first"),
            new TinyContentProvider(
                engineId: "second",
                value: 1
            )
        ));
        Assert.Empty(collection: registry.Build().Engines);
        Assert.Empty(collection: registry.Build().ContentProviders);
    }
    [Fact]
    public void AnEmptyHostCannotPrepareAnEngineFromAnotherCatalog() {
        var candidate = Fixtures.BuildDocument() with { ScreensRaw = null, MachinesRaw = [new WorldMachine(
                "cabinet",
                "gaming-brick",
                JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.configuration.v1", model = "cgb", boot = "fast" })
            )] };
        using var empty = new WorldMachineHost(
            [],
            new WorldMachineCatalog([])
        );
        using var installed = new WorldMachineHost(
            [],
            TestHookInstaller.CreateMachineCatalog()
        );

        Assert.False(condition: empty.TryPrepare(
            candidate: candidate,
            current: null,
            plan: out var refused,
            reason: out var reason
        ));
        Assert.Null(@object: refused);
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gaming-brick"
        );
        Assert.True(condition: installed.TryPrepare(
            candidate: candidate,
            current: null,
            plan: out var admitted,
            reason: out _
        ));
        admitted!.Dispose();
    }
    [Fact]
    public void IndependentHostsPrepareTheSameFormatThroughTheirOwnProviders() {
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-machine-{Guid.NewGuid():N}.tiny"
        );

        File.WriteAllBytes(
            bytes: [17],
            path: path
        );
        try {
            var firstEngine = new TinyEngine(id: "tiny");
            var secondEngine = new TinyEngine(id: "tiny");
            var rawEngine = new TinyEngine(id: "tiny");
            var firstProvider = new TinyContentProvider(
                engineId: "tiny",
                value: 41
            );
            var secondProvider = new TinyContentProvider(
                engineId: "tiny",
                value: 73
            );
            var firstDefinition = Fixtures.BuildDocument() with { ScreensRaw = null, MachinesRaw = [Machine(
                    engine: "tiny",
                    path: path
                )] };
            using var first = new WorldMachineHost(
                [],
                new WorldMachineCatalog(
                    contentProviders: [firstProvider],
                    engines: [firstEngine]
                )
            );
            using var second = new WorldMachineHost(
                [],
                new WorldMachineCatalog(
                    contentProviders: [secondProvider],
                    engines: [secondEngine]
                )
            );
            using var raw = new WorldMachineHost(
                [],
                new WorldMachineCatalog([rawEngine])
            );

            Assert.True(
                condition: first.TryPrepare(
                    candidate: firstDefinition,
                    current: null,
                    plan: out var firstPlan,
                    reason: out var firstReason
                ),
                userMessage: firstReason
            );
            using (firstPlan) { first.Commit(plan: firstPlan!); first.Finish(plan: firstPlan!); }
            Assert.True(
                condition: second.TryPrepare(
                    candidate: firstDefinition,
                    current: null,
                    plan: out var secondPlan,
                    reason: out var secondReason
                ),
                userMessage: secondReason
            );
            using (secondPlan) { second.Commit(plan: secondPlan!); second.Finish(plan: secondPlan!); }

            Assert.Equal(
                new byte[] { 41 },
                firstEngine.LoadedImage
            );
            Assert.Equal(
                new byte[] { 73 },
                secondEngine.LoadedImage
            );
            Assert.Equal(
                new byte[] { 41 },
                firstEngine.LoadedImage
            );
            Assert.Equal(
                new byte[] { 73 },
                secondEngine.LoadedImage
            );
        } finally {
            File.Delete(path: path);
        }
    }
    [Fact]
    public void OfflineChecksReportDeferralAndAdmissionUsesOnlyTheSuppliedCatalog() {
        var candidate = Fixtures.BuildDocument() with { ScreensRaw = null, MachinesRaw = [new WorldMachine(
                "cabinet",
                "gaming-brick",
                JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.configuration.v1", model = "cgb", boot = "fast" })
            )] };
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            candidate,
            machines: null,
            errors,
            deferred,
            out _
        ));
        Assert.Empty(collection: errors);
        Assert.Contains(
            "no machine catalog",
            Assert.Single(collection: deferred),
            StringComparison.Ordinal
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: candidate,
            machines: new WorldMachineCatalog([]),
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gaming-brick"
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: candidate,
                machines: TestHookInstaller.CreateMachineCatalog(),
                reason: out reason
            ),
            userMessage: reason
        );
    }
    [Fact]
    public void RegistrationSnapshotsAreIndependentAndDuplicateIdsRefuse() {
        var registry = new WorldMachineExtensionRegistry();
        var engine = new TinyEngine(id: "first");

        registry.RegisterEngine(engine);
        var before = registry.Build();

        registry.RegisterEngine(new TinyEngine(id: "second"));
        var after = registry.Build();

        Assert.False(condition: before.IsRegistered(engineId: "second"));
        Assert.True(condition: after.IsRegistered(engineId: "second"));
        Assert.Throws<ArgumentException>(testCode: () => registry.RegisterEngine(new TinyEngine(id: "first")));
        Assert.Same(
            engine,
            registry.Build().Engines["first"]
        );
    }

    private sealed class TinyContentProvider(string engineId, byte value) : IMachineContentProvider {
        public string EngineId => engineId;

        public PreparedMachineContent Prepare(ReadOnlyMemory<byte> content) => new(
            Image: [value],
            SourceHash: WorldDefinitionFileSource.ComputeContentHash(content: content.Span),
            Symbols: new Dictionary<string, MachineContentSymbol> { ["counter"] = new(
                Address: value,
                Space: "bus"
            ) }
        );
        public bool Recognizes(string contentPath) => contentPath.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: ".tiny"
        );
    }
    private sealed class TinyEngine(string id) : IMachineEngine {
        public string Id => id;
        public byte[]? LoadedImage { get; private set; }

        public MachineEngineDescriptor Descriptor { get; } = new(
            id,
            "Test runtime",
            new(
                "puck.tiny.config.v1",
                [
            new(
                        "content",
                        MachineFieldKind.Object,
                        "Content",
                        Fields: [
                new(
                                "path",
                                MachineFieldKind.String,
                                "Content path",
                                Required: true,
                                Role: MachineFieldRole.ContentPath
                            )
            ]
                    )
        ]
            ),
            [],
            [],
            [],
            [],
            []
        );
        public List<TinyMachine> Created { get; } = [];

        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
            LoadedImage = contentBytes;
            return new TinyMachine();
        }
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            LoadedImage = request.RequireAsset(fieldPath: "content.path").Image.ToArray();
            var runtime = new TinyMachine();

            Created.Add(item: runtime);
            return runtime;
        }
    }
    private sealed class TinyMachine : IMachineRuntime {
        public ulong ElapsedTicks { get; private set; }
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;

        public bool Advance(ulong deltaTicks) {
            ElapsedTicks += deltaTicks;
            return (deltaTicks != 0);
        }
        public void Dispose() { }
    }
}
