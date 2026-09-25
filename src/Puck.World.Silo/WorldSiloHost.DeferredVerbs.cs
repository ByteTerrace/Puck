using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    // The console's deferred-verb answers every admitted row's echoes reach, or null before the silo attaches them.
    private WorldDeferredVerbAnswers? m_deferredVerbAnswers;

    /// <summary>Answers the silo console's deferred verbs from every row this host admits from now on: each row's
    /// echoes reach <paramref name="answers"/> under the row's name, which answers and counts only a line the console
    /// registered on that row, as the World host does; a remote peer's refusal and the silo's own refused reload are
    /// not console lines and are not counted. Attach before any row activates.</summary>
    /// <param name="answers">The console's attached answers (<see cref="WorldSiloApplication.AnswerDeferredVerbs"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="answers"/> is <see langword="null"/>.</exception>
    public void AnswerDeferredVerbs(WorldDeferredVerbAnswers answers) {
        ArgumentNullException.ThrowIfNull(argument: answers);

        Volatile.Write(location: ref m_deferredVerbAnswers, value: answers);
    }

    // The console submits a row's lines through its own link, registered under the row's name; the silo's own reload
    // and the replay tape use the bare one, registering nothing. No console link before the silo attaches answers.
    private WorldConsoleServerLink? ConsoleLinkFor(LoopbackTransport link, string row) =>
        ((Volatile.Read(location: ref m_deferredVerbAnswers) is { } answers)
            ? link.ForConsole(row: answers.Echoes.ForRow(row: row))
            : null
        );
    // Taps an admitted row's echoes into the console's answers under the row's name, once the silo attached them.
    private void TapDeferredVerbs(WorldInstance row) {
        if (Volatile.Read(location: ref m_deferredVerbAnswers) is { } answers) {
            TapEchoes(
                answers: answers,
                row: row.Name,
                server: row.Server
            );
        }
    }

    /// <summary>Taps one row's server echoes into the console's deferred-verb answers, beside whatever else observes
    /// them; what the silo does with each row it admits.</summary>
    /// <param name="server">The admitted row's server.</param>
    /// <param name="answers">The console's attached answers.</param>
    /// <param name="row">The row's name, as its console link registers it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/>, <paramref name="answers"/> or
    /// <paramref name="row"/> is <see langword="null"/>.</exception>
    public static void TapEchoes(WorldServer server, WorldDeferredVerbAnswers answers, string row) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: answers);
        ArgumentNullException.ThrowIfNull(argument: row);

        server.EchoTap += echo => answers.Answer(
            echo: in echo,
            row: row
        );
    }
}
