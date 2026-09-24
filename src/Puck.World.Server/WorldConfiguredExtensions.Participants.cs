using System.Text.Json;
using Puck.Abstractions;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldConfiguredExtensions {
    private readonly List<(string Name, IWorldParticipant Participant)> m_participants = [];

    private bool m_participantsStarted;

    /// <summary>Gets each configured participant's name and live status line, without private settings.</summary>
    public IReadOnlyList<string> Participants => m_participants.Select(selector: static entry => $"{entry.Name}: {entry.Participant.Describe()}").ToArray();

    private void ConfigureParticipants(PuckExtensionSet extensions, IPrincipalServerLink? link) {
        if (m_configuration.Participants is not { Count: > 0 } rows) { return; }
        if (rows.Count > 16) { throw new ArgumentException(message: "At most 16 participants may be configured."); }
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var capacity = m_server.Definition.Population.Capacity;

        foreach (var row in rows) {
            ValidateName(name: row.Name);
            if (!names.Add(item: row.Name)) { throw new ArgumentException(message: $"Duplicate participant '{row.Name}'."); }
            if (row.Settings.ValueKind != JsonValueKind.Object) { throw new ArgumentException(message: $"Participant '{row.Name}' needs object settings."); }
            if ((row.Body < 0) || (row.Body >= capacity)) {
                throw new ArgumentException(message: $"Participant '{row.Name}' body {row.Body} is outside this world's population capacity {capacity}.");
            }
            var type = extensions.Select<WorldParticipantType>(
                key: row.Type,
                purpose: $"Participant '{row.Name}' type"
            );
            var principal = ParsePrincipal(text: row.Principal);
            var server = m_server;

            if (link is null) { throw new InvalidOperationException(message: $"Participant '{row.Name}' needs a world link that attributes reads to a principal; this host supplies none."); }
            m_participants.Add(item: (row.Name, type.Create(
                arg1: new WorldParticipantContext {
                    BodyIndex = row.Body,
                    Channels = () => server.Population.Channels,
                    Clock = m_clock,
                    Extensions = extensions,
                    Link = link,
                    Name = row.Name,
                    Principal = principal,
                },
                arg2: row.Settings
            )));
        }
    }
    private void DisposeParticipantsDuringRefusal() {
        foreach (var (_, participant) in m_participants.AsEnumerable().Reverse()) {
            // A refused composition never started a participant, so disposal has no worker to wait for.
            participant.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        m_participants.Clear();
    }
    private async ValueTask DisposeParticipantsAsync() {
        foreach (var (_, participant) in m_participants.AsEnumerable().Reverse()) {
            await participant.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    private void PumpParticipants(ulong completedTick) {
        foreach (var (_, participant) in m_participants) {
            participant.Pump(completedTick: completedTick);
        }
    }

    /// <summary>Starts the configured work: the shared operation worker when operations are configured, and every
    /// participant. Call once, when the host starts; later calls do nothing.</summary>
    /// <exception cref="ObjectDisposedException">The runtime has been disposed.</exception>
    public void Start() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        if (m_participantsStarted) { return; }
        m_participantsStarted = true;
        if (m_configuration.Operations.Count > 0) { Host.Start(); }
        foreach (var (_, participant) in m_participants) {
            participant.Start();
        }
    }
}
