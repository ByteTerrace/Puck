using Puck.Commands;
using Puck.World.Protocol;


namespace Puck.World.Testing;

/// <summary>The deferred-verb echo helpers: capturing the console, reading <c>wire.errors</c>, and evicting the first
/// deferred answer.</summary>
internal static class DeferredVerbFixtures {
    internal static (string Out, string Error) Captured(Action action) {
        var (originalOut, originalError) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Console.SetOut(newOut: output);
        Console.SetError(newError: error);
        try {
            action();
        } finally {
            Console.SetOut(newOut: originalOut);
            Console.SetError(newError: originalError);
        }

        return (output.ToString(), error.ToString());
    }
    // Registers correlation 1, then enough later ids to evict it.
    internal static void EvictTheFirst(WorldDeferredVerbEchoes echoes) => RegisterAfterTheFirst(
        echoes: echoes,
        later: WorldDeferredVerbEchoes.Capacity
    );
    internal static string WireErrors(CommandRegistry registry) => registry.Submit(line: "wire.errors").Output;
    // Registers correlation 1 as the evicted verb, then the given number of later ids, each evicting one earlier line.
    internal static void RegisterAfterTheFirst(WorldDeferredVerbEchoes echoes, long later) {
        for (var id = 1L; (id <= (later + 1L)); id++) {
            _ = echoes.Register(
                correlationId: id,
                row: WorldDeferredVerbEchoes.DefaultRow,
                settlement: new CommandSettlement(),
                verb: ((id == 1L) ? EvictedVerb : "world.reset")
            );
        }
    }

    internal const string EvictedVerb = "world.undo";
}
