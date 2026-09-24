using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Puck.Abstractions;
using Puck.Commands;
using Puck.Launcher;
using Puck.Storage;
using Puck.Testing;
using Puck.World.Server;
using Puck.World.Silo;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Every deadline the silo puts on a lifecycle request, a retirement, or a shutdown runs on its one clock,
/// <see cref="WorldSiloHost.Clock"/>: with no pump to finish the work, each ends exactly when its bound expires on a
/// <see cref="VirtualClock"/>, not before.</summary>
public sealed class WorldSiloDeadlineLawTests {
    // Shorter than every storage bound the reload reads through, so the route's own deadline is the one that ends it.
    private const int ShutdownSeconds = 5;

    private static readonly WorldAuthorityIdentity Pinned = new(
        Owner: Guid.Parse(input: "0a1b2c3d-0000-4000-8000-000000000002"),
        World: SafeName.Parse(candidate: "row")
    );

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static DefaultHttpContext Request(string method, string path) {
        var context = new DefaultHttpContext { RequestAborted = TestToken, RequestServices = new ServiceCollection().BuildServiceProvider() };

        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        return context;
    }
    private static WorldSiloHost Silo(string directory, VirtualClock clock, IObjectBlobStore store, BufferedConsoleOutput output, WorldSiloReleaseManagement? release = null) {
        var source = new TextCommandSource(new CommandRegistry(modules: []));

        return new(
            blobStore: store,
            definition: new(
                Worlds: [new(
                    Federation: new(KeyFile: Path.Combine(
                        path1: directory,
                        path2: "unused.key"
                    )),
                    Owner: Pinned.Owner,
                    Pinned: true,
                    World: Pinned.World
                )],
                Doors: new(Budget: 1),
                Store: new(
                    Settings: JsonElement.Parse(json: "{}"),
                    Type: "directory"
                ),
                StateDir: directory,
                Clustering: new(Kind: "Localhost"),
                Lifecycle: new(
                    HealthPort: 0,
                    ShutdownSeconds: ShutdownSeconds
                ),
                Release: release
            ),
            routing: new(
                source: () => source,
                tagging: new SiloConsoleTagging(output: output)
            ),
            storageTarget: new DirectoryObjectStorageTarget(rootPath: directory),
            timeProvider: clock
        );
    }

    /// <summary>A loopback drain nobody pumps answers 503 when <c>lifecycle.shutdownSeconds</c> expires, and never
    /// stops the application it did not retire.</summary>
    [Fact]
    public async Task DrainRoute_AnswersUnavailableWhenShutdownSecondsExpireOnTheSiloClock() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var clock = new VirtualClock();
        var silo = Silo(
            clock: clock,
            directory: directory.RootPath,
            output: output,
            store: new FakeObjectBlobStore()
        );
        using var instances = silo.Instances;
        var lifetime = new Lifetime();
        var context = Request(
            method: "POST",
            path: "/drain"
        );
        var handled = new WorldSiloLifecycleService(
            extensions: PuckExtensionSet.Compose(extensions: []),
            lifetime: lifetime,
            silo: silo
        ).HandleAsync(context: context);

        await clock.ExpireAsync(
            ct: TestToken,
            dueTime: TimeSpan.FromSeconds(seconds: ShutdownSeconds),
            pending: handled
        );
        await handled;

        Assert.Equal(
            actual: context.Response.StatusCode,
            expected: StatusCodes.Status503ServiceUnavailable
        );
        Assert.Equal(
            actual: lifetime.Stops,
            expected: 0
        );
    }
    /// <summary>A pinned reload whose published definition never arrives is abandoned when
    /// <c>lifecycle.shutdownSeconds</c> expires, before the read's own longer bound.</summary>
    [Fact]
    public async Task ReloadRoute_AnswersUnavailableWhenShutdownSecondsExpireOnTheSiloClock() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var clock = new VirtualClock();
        var store = new StallingObjectBlobStore(
            inner: new FakeObjectBlobStore(),
            stalls: static (_, _) => true
        );
        var silo = Silo(
            clock: clock,
            directory: directory.RootPath,
            output: output,
            store: store
        );
        using var instances = silo.Instances;
        var context = Request(
            method: "POST",
            path: "/reload"
        );
        var handled = new WorldSiloLifecycleService(
            extensions: PuckExtensionSet.Compose(extensions: []),
            lifetime: new Lifetime(),
            silo: silo
        ).HandleAsync(context: context);

        await store.Stalled.WaitAsync(cancellationToken: TestToken);
        await clock.ExpireAsync(
            ct: TestToken,
            dueTime: TimeSpan.FromSeconds(seconds: ShutdownSeconds),
            pending: handled
        );
        await handled;

        Assert.Equal(
            actual: context.Response.StatusCode,
            expected: StatusCodes.Status503ServiceUnavailable
        );
    }
    /// <summary>A managed release request whose group record never arrives ends when <c>lifecycle.shutdownSeconds</c>
    /// expires.</summary>
    [Fact]
    public async Task ReleaseControl_EndsWhenShutdownSecondsExpireOnTheSiloClock() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var clock = new VirtualClock();
        var store = new StallingObjectBlobStore(
            inner: new FakeObjectBlobStore(),
            stalls: static (_, _) => true
        );
        var silo = Silo(
            clock: clock,
            directory: directory.RootPath,
            output: output,
            release: new(
                ExpectedRelease: "release-a",
                Group: "group",
                Owner: Pinned.Owner
            ),
            store: store
        );
        using var instances = silo.Instances;
        var handled = new WorldSiloReleaseControl(silo: silo).HandleAsync(context: Request(
            method: "GET",
            path: "/release/status"
        ));

        await store.Stalled.WaitAsync(cancellationToken: TestToken);
        await clock.ExpireAsync(
            ct: TestToken,
            dueTime: TimeSpan.FromSeconds(seconds: ShutdownSeconds),
            pending: handled
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => handled);
    }
    /// <summary>A refused activation releases the fence it acquired within
    /// <see cref="WorldSiloHost.ReleaseActivationTimeout"/> on the silo clock, before the store call's own longer bound;
    /// a release the store never answers ends there.</summary>
    [Fact]
    public async Task RefusedActivation_StopsReleasingItsFenceWhenTheTimeoutExpiresOnTheSiloClock() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var clock = new VirtualClock();
        var writes = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        // Acquisition writes the authority root once; the release's write is the second write to that same key.
        var store = new StallingObjectBlobStore(
            inner: new FakeObjectBlobStore(),
            stalls: (call, key) => ((call == StoreCall.Write) && ((writes[key] = (writes.GetValueOrDefault(key: key) + 1)) == 2))
        );
        var silo = Silo(
            clock: clock,
            directory: directory.RootPath,
            output: output,
            store: store
        );
        using var instances = silo.Instances;
        var activation = silo.ActivateAsync(
            ct: TestToken,
            identity: Pinned
        );
        var pumped = WorldSiloHost.PumpActivationMailboxesAsync(
            cancellationToken: TestToken,
            hosts: [silo],
            operation: activation
        );

        await store.Stalled.WaitAsync(cancellationToken: TestToken);
        await clock.ExpireAsync(
            ct: TestToken,
            dueTime: WorldSiloHost.ReleaseActivationTimeout,
            pending: activation
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => activation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => pumped);
    }
    /// <summary>A retirement drains by the instant the observer names, read on the silo clock, and stops the
    /// application once that instant passes whether or not the drain finished.</summary>
    [Fact]
    public async Task Retirement_EndsAtItsInstantOnTheSiloClock() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var clock = new VirtualClock();
        var silo = Silo(
            clock: clock,
            directory: directory.RootPath,
            output: output,
            store: new FakeObjectBlobStore()
        );
        using var instances = silo.Instances;
        var lifetime = new Lifetime();
        var available = TimeSpan.FromSeconds(seconds: 25);
        var retired = new WorldSiloLifecycleService(
            extensions: PuckExtensionSet.Compose(extensions: []),
            lifetime: lifetime,
            silo: silo
        ).RetireAsync(
            ct: TestToken,
            notBefore: (clock.GetUtcNow() + available)
        );

        await clock.ExpireAsync(
            ct: TestToken,
            dueTime: available,
            pending: retired
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => retired);
        Assert.Equal(
            actual: lifetime.Stops,
            expected: 1
        );
    }
    /// <summary>Host shutdown drains within <c>lifecycle.shutdownSeconds</c> on the silo clock; a drain nobody pumps
    /// fails the process when it expires.</summary>
    [Fact]
    public async Task Stopping_FailsTheProcessWhenShutdownSecondsExpireOnTheSiloClock() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var clock = new VirtualClock();
        var silo = Silo(
            clock: clock,
            directory: directory.RootPath,
            output: output,
            store: new FakeObjectBlobStore()
        );
        using var instances = silo.Instances;
        var exitCode = Environment.ExitCode;

        try {
            var stopping = new WorldSiloLifecycleService(
                extensions: PuckExtensionSet.Compose(extensions: []),
                lifetime: new Lifetime(),
                silo: silo
            ).StoppingAsync(cancellationToken: TestToken);

            await clock.ExpireAsync(
                ct: TestToken,
                dueTime: TimeSpan.FromSeconds(seconds: ShutdownSeconds),
                pending: stopping
            );
            await stopping;

            Assert.Equal(
                actual: Environment.ExitCode,
                expected: 1
            );
        } finally {
            Environment.ExitCode = exitCode;
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime {
        public int Stops;

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;

        public void StopApplication() => Interlocked.Increment(location: ref Stops);
    }
}
