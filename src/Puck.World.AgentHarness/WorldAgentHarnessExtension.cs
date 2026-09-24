using Puck.Abstractions;
using Puck.World.Agents.Harness;
using Puck.World.Protocol;

[assembly: PuckExtension(typeof(WorldAgentHarnessExtension))]

namespace Puck.World.Agents.Harness;

/// <summary>Contributes the <c>agent.harness</c> participant type: a host-approved extension configuration that lists
/// an <c>agent.harness</c> participant runs a Harness agent over the installed <see cref="ChatClientProvider"/> it
/// selects, driving its body through <see cref="WorldAgentBridge"/>. Registration makes no service call and starts no
/// work; without a configured participant the extension does nothing.</summary>
public sealed class WorldAgentHarnessExtension : IPuckExtension {
    /// <inheritdoc/>
    public string Name => "Puck.World.AgentHarness";

    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        registry.AddParticipant(participant: new WorldParticipantType(
            Create: WorldAgentParticipant.Create,
            Type: WorldAgentParticipant.Type
        ));
    }
}
