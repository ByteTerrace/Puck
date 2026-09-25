using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The host half of a <see cref="WorldDeferredVerbEchoes"/> table, shared by every composition root that
/// registers one: prints each late answer the table raises and counts each refusal once in the registry
/// <c>wire.errors</c> reads. A late typed verdict (<see cref="WorldDeferredVerbEchoes.Completed"/>) prints on stderr
/// when it is an error and on stdout otherwise; an eviction (<see cref="WorldDeferredVerbEchoes.Evicted"/>) is its
/// line's only answer, so it prints on stderr and counts. A host whose authority echoes reach it passes each echo to
/// <see cref="Answer"/>, which prints the registered line's verdict and counts a refusal unless an eviction already
/// answered that line.</summary>
/// <remarks>Lines go to <see cref="Console.Out"/> and <see cref="Console.Error"/> as they are when a line is written,
/// so a host that swaps the console writers after composing (the silo's line tagging) still frames them.</remarks>
public sealed class WorldDeferredVerbAnswers {
    private readonly WorldDeferredVerbEchoes m_echoes;
    private readonly CommandRegistry m_registry;

    private WorldDeferredVerbAnswers(WorldDeferredVerbEchoes echoes, CommandRegistry registry) {
        m_echoes = echoes;
        m_registry = registry;
    }

    /// <summary>Subscribes a host to its table's late answers and evictions.</summary>
    /// <param name="echoes">The host's pending-verb table.</param>
    /// <param name="registry">The registry whose refusal count <c>wire.errors</c> reports.</param>
    /// <returns>The attached answers, whose <see cref="Answer"/> the host's echo tap calls.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="echoes"/> or <paramref name="registry"/> is
    /// <see langword="null"/>.</exception>
    public static WorldDeferredVerbAnswers Attach(WorldDeferredVerbEchoes echoes, CommandRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: echoes);
        ArgumentNullException.ThrowIfNull(argument: registry);

        var answers = new WorldDeferredVerbAnswers(echoes: echoes, registry: registry);

        echoes.Completed += static result => Write(isError: result.IsError, line: result.Output);
        echoes.Evicted += answers.AnswerEviction;

        return answers;
    }
    /// <summary>Answers one authority echo: settles and prints the local line it answers, and counts a refusal no
    /// line's own dispatch could report. A refusal answering a line the table evicted is not counted again, since
    /// its eviction was already counted.</summary>
    /// <param name="echo">The authority's echo.</param>
    public void Answer(in WorldEditEcho echo) {
        if (m_echoes.Settle(echo: in echo) is { } verdict) {
            Write(isError: echo.Rejected, line: verdict);
        } else if (m_echoes.TakeEvicted(echo: in echo)) {
            return;
        }

        // A submitted edit is accepted into the tick queue and refused a tick later, so the registry's own
        // dispatch accounting never saw the refusal; a line refused synchronously never reaches the server and so
        // never echoes.
        if (echo.Rejected) {
            m_registry.NoteDeferredRejection();
        }
    }

    private void AnswerEviction(CommandResult result) {
        Write(isError: true, line: result.Output);
        m_registry.NoteDeferredRejection();
    }
    private static void Write(bool isError, string line) {
        if (isError) {
            Console.Error.WriteLine(value: line);
        } else {
            Console.WriteLine(value: line);
        }
    }
}
