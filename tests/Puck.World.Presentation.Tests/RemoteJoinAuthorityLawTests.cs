using System.Collections.Concurrent;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>Laws for the namespace a run names for the authority it connects to (<c>--connect-authority</c>, carried to
/// <see cref="WorldInstanceHost.JoinRemote"/>). The remote authority must prove that namespace at its handshake,
/// whatever the document the run booted declares: a run that names the authority's own namespace joins although its
/// document declares none, and a run that names another is refused by name although its document declares the right
/// one.</summary>
public sealed class RemoteJoinAuthorityLawTests {
    // Every line a host narrated, read from the law's thread while a federation lane narrates from its own.
    private sealed class NarratedLines : IWorldNarrationSink {
        private readonly ConcurrentQueue<string> m_lines = new();

        public string[] Lines => [.. m_lines];

        public void Narrate(in WorldNarration narration) => m_lines.Enqueue(item: narration.Text);
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ARemoteJoinRequiresTheNamespaceTheRunNames(bool namesTheAuthority) {
        const string Other = "another-authority";
        var boot = Fixtures.BuildDocument();

        // One eager seat, so the join sends the boot party as soon as that seat stands in the boot world.
        using var hosts = FederatedHosts.Build(sourceDefinition: boot with {
            PopulationRaw = boot.Population with {
                SeatActivationRaw = [SeatActivationPolicy.Eager, SeatActivationPolicy.OnDemand, SeatActivationPolicy.OnDemand, SeatActivationPolicy.OnDemand],
            },
        });
        var narrated = new NarratedLines();
        using var attached = hosts.Source.AttachNarrationSink(sink: narrated);
        var destination = hosts.DestinationRow.Server;

        if (namesTheAuthority) {
            // The document the run boots declares no namespace: only the run's own input names the authority.
            var definition = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: hosts.DestinationPath));

            File.WriteAllBytes(path: hosts.DestinationPath, bytes: WorldDefinitionSerialization.Serialize(definition: definition with {
                HostRaw = definition.Host with { AuthorityNamespace = null },
            }));
        }
        hosts.JoinSeat(slot: 0, travelTurn: default);
        hosts.Source.JoinRemote(
            authority: hosts.Door.ListenEndpoint!,
            authorityNamespace: (namesTheAuthority ? FederatedHosts.DestinationName : Other),
            documentPath: hosts.DestinationPath
        );
        var mismatch = $"federation destination authority mismatch: expected '{Other}', received '{FederatedHosts.DestinationName}'";
        bool SeatAway() => (hosts.Seats.RoutedEndpoint(slot: 0)?.Identity != WorldInstanceHost.BootInstanceName);
        bool Refused() => narrated.Lines.Any(predicate: line => line.Contains(value: mismatch, comparisonType: StringComparison.Ordinal));
        using var deadline = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 30));

        while (!(namesTheAuthority ? SeatAway() : Refused())) {
            Assert.False(condition: deadline.IsCancellationRequested, userMessage: $"the join was neither accepted nor refused by name; narrated: {string.Join(separator: " | ", value: narrated.Lines)}");
            _ = hosts.Source.FollowRemoteJoin();
            hosts.Source.DrainPendingTransfers();
            hosts.Source.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            if (!namesTheAuthority) {
                // A lane names why it is unavailable once its retry after the first failed handshake fails too, and
                // that retry waits on the hosts' clock, which only a law advances.
                hosts.Clock.Advance(by: WorldRemoteAuthority.ConnectRetryDelay);
            }
            Thread.Sleep(millisecondsTimeout: 1);
        }
        var landed = destination.ExecuteAuthorityOperation(operation: () => destination.Population.RemoteHumanCount);

        if (namesTheAuthority) {
            Assert.Equal(expected: 1, actual: landed);
            Assert.DoesNotContain(collection: narrated.Lines, filter: static line => line.Contains(value: "authority mismatch", comparisonType: StringComparison.Ordinal));
        } else {
            // The seat stays in the boot world, and nothing of it reached the authority that proved another name.
            Assert.False(condition: SeatAway());
            Assert.True(condition: hosts.SourceRow.Server.Population.IsActive(index: 0));
            Assert.Equal(expected: 0, actual: landed);
        }
    }
}
