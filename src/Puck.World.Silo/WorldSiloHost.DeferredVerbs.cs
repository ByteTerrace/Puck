using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    // The console's deferred-verb answers every admitted row's echoes reach, or null before the silo attaches them.
    private WorldDeferredVerbAnswers? m_deferredVerbAnswers;

    /// <summary>Answers the silo console's deferred verbs from every row this host admits from now on: each row's
    /// echoes reach <paramref name="answers"/>, which settles and prints the line an echo answers and counts a late
    /// refusal in <c>wire.errors</c>, as the World host counts one. Attach before any row activates.</summary>
    /// <param name="answers">The console's attached answers (<see cref="WorldSiloApplication.AnswerDeferredVerbs"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="answers"/> is <see langword="null"/>.</exception>
    public void AnswerDeferredVerbs(WorldDeferredVerbAnswers answers) {
        ArgumentNullException.ThrowIfNull(argument: answers);

        Volatile.Write(location: ref m_deferredVerbAnswers, value: answers);
    }
    /// <summary>Taps one row's server echoes into the console's deferred-verb answers, beside whatever else observes
    /// them; what the silo does with each row it admits.</summary>
    /// <param name="server">The admitted row's server.</param>
    /// <param name="answers">The console's attached answers.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="answers"/> is
    /// <see langword="null"/>.</exception>
    public static void TapEchoes(WorldServer server, WorldDeferredVerbAnswers answers) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: answers);

        server.EchoTap += echo => answers.Answer(echo: in echo);
    }
}
