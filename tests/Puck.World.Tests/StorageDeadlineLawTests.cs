using System.Text;

using Azure.Core;

using Puck.Storage;
using Puck.Testing;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Every bound a World host puts on one storage call runs on the host's clock: a call the store never answers
/// ends exactly when its bound expires on a <see cref="VirtualClock"/>, not before, and is refused by the caller's own
/// name for a failed read.</summary>
public sealed class StorageDeadlineLawTests {
    private static readonly Guid Owner = Guid.Parse(input: "0a1b2c3d-0000-4000-8000-000000000001");
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");
    private static readonly SafeName World = SafeName.Parse(candidate: "amber");

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static StallingObjectBlobStore StallEveryCall() => new(
        inner: new FakeObjectBlobStore(),
        stalls: static (_, _) => true
    );

    [Fact]
    public async Task AuthorityStoreCall_EndsWhenItsOperationTimeoutExpiresOnTheHostClock() {
        var clock = new VirtualClock();
        var stalling = StallEveryCall();
        var store = new WorldAuthorityBlobStore(
            store: stalling,
            target: Target,
            timeProvider: clock
        );
        var load = store.LoadRootAsync(
            cancellationToken: TestToken,
            identity: new WorldAuthorityIdentity(
                Owner: Owner,
                World: World
            )
        );

        await stalling.Stalled.WaitAsync(cancellationToken: TestToken);
        await clock.ExpireAsync(
            ct: TestToken,
            dueTime: WorldAuthorityBlobStore.OperationTimeout,
            pending: load
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => load);
    }
    [Fact]
    public async Task CredentialProbe_TimesOutWhenItsTimeoutExpiresOnTheHostClock() {
        var clock = new VirtualClock();
        var credential = new StallingCredential();
        var timeout = TimeSpan.FromSeconds(seconds: 3);
        var probe = AzureBlobCredentialProbe.ProbeAsync(
            cancellationToken: TestToken,
            clock: clock,
            credential: credential,
            timeout: timeout
        ).AsTask();

        await credential.Entered.Task.WaitAsync(cancellationToken: TestToken);
        await clock.ExpireAsync(
            ct: TestToken,
            dueTime: timeout,
            pending: probe
        );

        var status = await probe;

        Assert.False(condition: status.Available);
        Assert.Equal(
            actual: status.Detail,
            expected: "timed out after 3s"
        );
    }
    [Fact]
    public async Task HostedDefinitionRead_IsRefusedWhenItsOperationTimeoutExpiresOnTheHostClock() {
        var clock = new VirtualClock();
        var stalling = StallEveryCall();
        var origin = new WorldHostedOrigin(
            owner: Owner,
            store: stalling,
            target: Target,
            timeProvider: clock,
            world: World
        );
        var load = origin.LoadAsync(
            cancellationToken: TestToken,
            instanceIdentity: World.Value
        ).AsTask();

        await stalling.Stalled.WaitAsync(cancellationToken: TestToken);
        await clock.ExpireAsync(
            ct: TestToken,
            dueTime: WorldHostedOrigin.OperationTimeout,
            pending: load
        );

        var (definition, reason) = await load;

        Assert.Null(@object: definition);
        Assert.StartsWith(
            actualString: reason,
            expectedStartString: "could not read "
        );
    }
    /// <summary>Each of the resolver's three bounds — a catalog read, a hosted read, and the whole basis chain behind a
    /// catalog read that answered — refuses the neighbour as unavailable when it expires, naming what timed out.</summary>
    [Theory]
    [InlineData(WorldStorageNamespace.Worlds, false, "timed out after 15s reading ")]
    [InlineData(WorldStorageNamespace.Hosted, false, "could not read ")]
    [InlineData(WorldStorageNamespace.Worlds, true, "basis chain refused: timed out reading ")]
    public async Task NeighbourRead_IsUnavailableWhenItsOperationTimeoutExpiresOnTheHostClock(WorldStorageNamespace @namespace, bool stallTheBasis, string expected) {
        var clock = new VirtualClock();
        var inner = new FakeObjectBlobStore();
        var tipKey = WorldOwnedWorldSync.AddressFor(
            containerId: Owner,
            id: World
        ).Key;
        var basisKey = WorldOwnedWorldSync.BasisAddressFor(
            containerId: Owner,
            id: SafeName.Parse(candidate: "deep")
        ).Key;

        inner.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "deep" }"""),
            key: tipKey,
            objectId: Owner
        );

        var stalling = new StallingObjectBlobStore(
            inner: inner,
            stalls: (_, key) => (!stallTheBasis || (key == basisKey))
        );
        var resolver = new WorldStorageNeighbourResolver(
            containerId: Owner,
            @namespace: @namespace,
            store: stalling,
            target: Target,
            timeProvider: clock
        );
        var resolution = Task.Run(
            cancellationToken: TestToken,
            function: () => resolver.Resolve(document: World.Value)
        );

        await stalling.Stalled.WaitAsync(cancellationToken: TestToken);
        await clock.ExpireAsync(
            ct: TestToken,
            dueTime: WorldStorageNeighbourResolver.OperationTimeout,
            pending: resolution
        );

        var resolved = await resolution;

        Assert.Equal(
            actual: resolved.Kind,
            expected: WorldNeighbourResolutionKind.Unavailable
        );
        Assert.Contains(
            actualString: resolved.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: expected
        );
    }

    private sealed class StallingCredential : TokenCredential {
        public TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => StallingObjectBlobStore.StallUntilCanceledAsync<AccessToken>(
            cancellationToken: cancellationToken,
            entered: Entered
        );
    }
}
