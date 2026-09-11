using Puck.Abstractions;
using Puck.World.Agents.Harness;
using Puck.World.Protocol;

[assembly: PuckExtension(typeof(WorldAgentHarnessExtension))]

namespace Puck.World.Agents.Harness;

/// <summary>
/// First-class dynamic extension providing Microsoft Agent Framework Harness autonomous participant runners.
/// </summary>
public sealed class WorldAgentHarnessExtension : IWorldAgentExtension {
    /// <inheritdoc/>
    public string Name => "Puck.World.AgentHarness";

    /// <inheritdoc/>
    public void Register(IWorldAgentExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.RegisterAgentRunner(factory: static services => {
            return new WorldAgentHostedService(services: services);
        });
    }

    private sealed class WorldAgentHostedService(IServiceProvider services) : IPuckHostedService {
        private readonly IServiceProvider m_services = (services ?? throw new ArgumentNullException(paramName: nameof(services)));
        private CancellationTokenSource? m_cts;

        public IServiceProvider Services => m_services;

        public Task StartAsync(CancellationToken cancellationToken) {
            m_cts = new CancellationTokenSource();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) {
            m_cts?.Cancel();

            return Task.CompletedTask;
        }

        public void Dispose() {
            m_cts?.Dispose();
            m_cts = null;
        }

        public ValueTask DisposeAsync() {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
