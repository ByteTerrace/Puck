using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The host half of a <see cref="WorldDeferredVerbEchoes"/> table, shared by every composition root that
/// registers one: prints each answer the table owes a registered console line and counts each refusal once in the
/// registry <c>wire.errors</c> reads. <c>wire.errors</c> counts a refused registered line and a console link's codec
/// refusal, and nothing else: an echo no console line registered (a remote peer's, a rule's or addon's, a host's own
/// reload, a grant-table replay) is neither printed nor counted. A late typed mutation result
/// (<see cref="WorldDeferredVerbEchoes.Completed"/>) prints on stderr when it is an error and on stdout otherwise; it
/// never counts, since the verdict of the line it answers does.</summary>
/// <remarks>Lines go to <see cref="Console.Out"/> and <see cref="Console.Error"/> as they are when a line is written,
/// so a host that swaps the console writers after composing (the silo's line tagging) still frames them.</remarks>
public sealed class WorldDeferredVerbAnswers {
    private readonly WorldDeferredVerbEchoes m_echoes;

    private WorldDeferredVerbAnswers(WorldDeferredVerbEchoes echoes) =>
        m_echoes = echoes;

    /// <summary>Gets the console's table these answers print and count.</summary>
    public WorldDeferredVerbEchoes Echoes => m_echoes;

    /// <summary>Subscribes a host to its table's answers.</summary>
    /// <param name="echoes">The console's table.</param>
    /// <param name="registry">The registry whose refusal count <c>wire.errors</c> reports.</param>
    /// <returns>The attached answers, whose <see cref="Answer"/> each row's echo tap calls.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="echoes"/> or <paramref name="registry"/> is
    /// <see langword="null"/>.</exception>
    public static WorldDeferredVerbAnswers Attach(WorldDeferredVerbEchoes echoes, CommandRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: echoes);
        ArgumentNullException.ThrowIfNull(argument: registry);

        echoes.Completed += static result => Write(isError: result.IsError, line: result.Output);
        echoes.Answered += answer => {
            if (answer.Line is { } line) {
                Write(isError: answer.IsError, line: line);
            }
            if (answer.Counts) {
                registry.NoteDeferredRejection();
            }
        };

        return new WorldDeferredVerbAnswers(echoes: echoes);
    }
    /// <summary>Answers one echo from <paramref name="row"/>'s authority
    /// (<see cref="WorldDeferredVerbSettlement.Answer"/>).</summary>
    /// <param name="echo">The authority's echo.</param>
    /// <param name="row">The row whose authority raised it.</param>
    public void Answer(in WorldEditEcho echo, string row) => m_echoes.Answer(
        echo: in echo,
        row: row
    );

    private static void Write(bool isError, string line) {
        if (isError) {
            Console.Error.WriteLine(value: line);
        } else {
            Console.WriteLine(value: line);
        }
    }
}
