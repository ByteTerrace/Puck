using Microsoft.Extensions.Hosting;
using Puck.Commands;

namespace Puck.Launcher;

/// <summary>
/// Reads command lines from standard input and feeds them to a <see cref="TextCommandSource"/>, so a piped
/// script or an interactive terminal drives the command system like any other source.
/// </summary>
/// <remarks>
/// Console reads are blocking and not reliably cancellable, so the loop runs on a dedicated background
/// thread and <see cref="ExecuteAsync"/> returns immediately — host shutdown never waits on a pending
/// read, and the thread dies with the process. The loop ends on its own at end-of-input (a closed pipe
/// or no attached console) or when shutdown is signalled.
/// </remarks>
public sealed class StandardInputReaderService : BackgroundService {
    private readonly TextCommandSource m_source;
    private readonly string m_threadName;

    /// <summary>Initializes a new instance of the <see cref="StandardInputReaderService"/> class.</summary>
    /// <param name="source">The text source each read command line is enqueued onto.</param>
    /// <param name="threadName">The name of the dedicated reader thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public StandardInputReaderService(TextCommandSource source, string threadName = "Puck Stdin Reader") {
        ArgumentNullException.ThrowIfNull(source);

        m_source = source;
        m_threadName = threadName;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        var readerThread = new Thread(start: () => ReadLoop(stoppingToken: stoppingToken)) {
            IsBackground = true,
            Name = m_threadName,
        };

        readerThread.Start();

        return Task.CompletedTask;
    }

    private void ReadLoop(CancellationToken stoppingToken) {
        try {
            var input = Console.In;

            while (!stoppingToken.IsCancellationRequested) {
                var line = input.ReadLine();

                if (line is null) {
                    break;
                }

                m_source.Enqueue(line: line);
            }
        } catch (IOException) {
            // No readable console (for example, a windowed launch with no attached terminal): there is
            // simply nothing to drive from, so the reader stops.
        }
    }
}
