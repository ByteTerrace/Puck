using System.Globalization;
using Puck.Commands;

namespace Puck.World.Server;

/// <summary>
/// The noninterference harness's hold on a world's edit echo, the channel a buffered act's verdict reaches its actor
/// through (a fire-and-forget console write answers nothing else): every echo whose acting principal an observed seat
/// is, recorded as that seat received it, and every refusal of an operator act, which leaves the run invalid, since an
/// operator act is a variant's setup and a refused one never authored the hidden state the pair is about. It chains
/// whatever tap the host had installed and restores it when disposed.
/// </summary>
public sealed class WorldDisclosureEchoTap : IDisposable {
    private readonly WorldServer m_server;
    private readonly Action<WorldEditEcho>? m_previous;
    private readonly Action<WorldEditEcho> m_tap;

    private WorldDisclosureEchoTap(WorldServer server, WorldDisclosureTranscript transcript, IReadOnlyDictionary<Principal, string> observed, Func<ulong> tick) {
        m_server = server;
        m_previous = server.EchoTap;
        m_tap = echo => {
            m_previous?.Invoke(obj: echo);
            var actor = echo.ActingPrincipal;

            if (observed.TryGetValue(key: actor, value: out var label)) {
                transcript.RecordEcho(echo: in echo, principal: label);
            } else if ((actor == Principal.Console) && echo.Rejected) {
                transcript.Invalidate(reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"an operator act was refused at tick {tick()}: {echo.Message}"));
            }
        };
        server.EchoTap = m_tap;
    }

    /// <summary>Installs the tap on <paramref name="server"/>.</summary>
    /// <param name="server">The world.</param>
    /// <param name="transcript">The transcript echoes and refusals are recorded into.</param>
    /// <param name="observed">Each observed seat's principal and its label in the transcript.</param>
    /// <returns>The tap; disposing it restores the host's own.</returns>
    public static WorldDisclosureEchoTap Attach(WorldServer server, WorldDisclosureTranscript transcript, IReadOnlyDictionary<Principal, string> observed) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: transcript);
        ArgumentNullException.ThrowIfNull(argument: observed);
        return new WorldDisclosureEchoTap(observed: observed, server: server, tick: () => server.CompletedTick, transcript: transcript);
    }

    /// <summary>Restores the host's tap, unless another has replaced this one since.</summary>
    public void Dispose() {
        if (ReferenceEquals(objA: m_server.EchoTap, objB: m_tap)) { m_server.EchoTap = m_previous; }
    }
}
