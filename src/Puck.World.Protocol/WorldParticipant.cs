using Puck.Commands;
using System.Text.Json;
using Puck.Abstractions;

namespace Puck.World.Protocol;

/// <summary>An installed participant type: an autonomous actor a host runs in a world as one principal over one body,
/// selected by key from a host-approved configuration. Configuration never supplies executable paths.</summary>
/// <param name="Type">The stable catalog key.</param>
/// <param name="Create">Validates the settings and constructs the participant without starting work or calling a
/// service. Refuses invalid settings with an <see cref="ArgumentException"/> naming the setting, and an uninstalled
/// contribution it selects with a <see cref="PuckExtensionException"/> or <see cref="ArgumentException"/> naming the
/// installed ones.</param>
public sealed record WorldParticipantType(string Type, Func<WorldParticipantContext, JsonElement, IWorldParticipant> Create);
/// <summary>What a host hands a participant: its identity, the world link its reads and actions cross, and the live
/// channel vocabulary. Every member is fixed for the participant's lifetime.</summary>
public sealed class WorldParticipantContext {
    /// <summary>Gets the 0-based body the participant controls.</summary>
    public required int BodyIndex { get; init; }
    /// <summary>Gets the live world's compiled channel table. Call it only from <see cref="IWorldParticipant.Pump"/>'s
    /// thread; it reflects the current definition after a reload.</summary>
    public required Func<WorldChannelTable> Channels { get; init; }
    /// <summary>Gets the host clock a participant paces its own work on. Simulation never reads it.</summary>
    public required TimeProvider Clock { get; init; }
    /// <summary>Gets the host's composed extensions, from which a participant selects the contributions it needs.</summary>
    public required PuckExtensionSet Extensions { get; init; }
    /// <summary>Gets the world link every read and action crosses, stamped with <see cref="Principal"/>. Use it only
    /// from <see cref="IWorldParticipant.Pump"/>'s thread.</summary>
    public required IPrincipalServerLink Link { get; init; }
    /// <summary>Gets the configured participant name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the acting principal.</summary>
    public required Principal Principal { get; init; }
}
/// <summary>A configured participant. The host starts it with the extension runtime, pumps it at every closed
/// simulation boundary, and disposes it when the runtime stops.</summary>
public interface IWorldParticipant : IAsyncDisposable {
    /// <summary>Describes the participant's live status for <c>world.extensions</c>, without private settings.</summary>
    /// <returns>One line of status.</returns>
    string Describe();
    /// <summary>Runs the participant's queued world operations on the simulation thread at a closed boundary. Must be
    /// short: no service calls, no waiting.</summary>
    /// <param name="completedTick">The world's completed tick.</param>
    void Pump(ulong completedTick);
    /// <summary>Starts the participant's own worker. Called once, when the host starts.</summary>
    void Start();
}
/// <summary>Registers <see cref="WorldParticipantType"/> contributions.</summary>
public static class WorldParticipantRegistration {
    /// <summary>Adds a participant type under its <see cref="WorldParticipantType.Type"/>.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="participant">The participant type.</param>
    /// <exception cref="PuckExtensionException">Another registration holds the key.</exception>
    public static void AddParticipant(this IPuckExtensionRegistry registry, WorldParticipantType participant) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        ArgumentNullException.ThrowIfNull(argument: participant);
        registry.Add(
            contribution: participant,
            key: participant.Type
        );
    }
}
