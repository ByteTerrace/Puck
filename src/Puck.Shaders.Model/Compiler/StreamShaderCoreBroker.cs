using System.Globalization;

namespace Puck.Shaders;

/// <summary>
/// Asks a build host for cores over a pair of text streams, the build's standard output and input under
/// <c>build/Shaders.targets</c>: a request is the line <c>##puck-cores request &lt;n&gt;</c>, answered in order by the line
/// <c>granted &lt;k&gt;</c>, and a release is the line <c>##puck-cores release &lt;n&gt;</c>, which needs no answer.
/// </summary>
/// <remarks>
/// <para>Answers are read on a thread of the broker's own, so waiting for a grant never occupies the build that asked:
/// the build keeps admitting work on the cores it holds and giving back the ones its finished compiles free, and the
/// host, whose own budget may be waiting for exactly those releases, can always answer. A synchronous read on the
/// asking thread (and <see cref="Console.In"/> reads synchronously even when asked asynchronously) instead stops the
/// build until the host answers, and the host cannot answer until the build gives a core back.</para>
/// <para>A request abandoned by its cancellation token is still answered by the host; the broker releases that grant as
/// soon as it arrives, so no core stays held for a request nobody waits on. When the answers end, every pending request
/// fails with an <see cref="IOException"/>.</para>
/// </remarks>
public sealed class StreamShaderCoreBroker : IShaderCoreBroker {
    /// <summary>The prefix of every line the broker writes.</summary>
    public const string Prefix = "##puck-cores ";

    private const string Granted = "granted ";

    private readonly TextWriter m_requests;

    private readonly Lock m_gate = new();
    private readonly Queue<TaskCompletionSource<int>> m_pending = new();

    private bool m_ended;

    /// <summary>Initializes a broker that writes its requests and releases to <paramref name="requests"/> and reads the
    /// host's answers from <paramref name="answers"/>, on a background thread it starts now.</summary>
    /// <param name="answers">The host's answers, one a line.</param>
    /// <param name="requests">Receives each request and release as one line. Writes are serialized on the writer
    /// itself, so a log sharing it keeps whole lines.</param>
    public StreamShaderCoreBroker(TextReader answers, TextWriter requests) {
        ArgumentNullException.ThrowIfNull(argument: answers);
        ArgumentNullException.ThrowIfNull(argument: requests);
        m_requests = requests;
        new Thread(start: () => Read(answers: answers)) { IsBackground = true, Name = "Puck shader core answers" }.Start();
    }

    /// <inheritdoc/>
    public Task<int> RequestAsync(int count, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: count, other: 1);

        var pending = new TaskCompletionSource<int>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        lock (m_gate) {
            if (m_ended) {
                return Task.FromException<int>(exception: new IOException(message: "The build host has stopped answering core requests."));
            }
            // Queued before the line is written, so an answer can never arrive ahead of its request.
            m_pending.Enqueue(item: pending);
        }
        Write(line: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Prefix}request {count}"));
        // A cancelled request stays queued: the host still answers it, and Read gives that grant straight back.
        var registration = cancellationToken.Register(callback: static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(), state: pending);

        _ = pending.Task.ContinueWith(
            continuationAction: (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
            scheduler: TaskScheduler.Default,
            state: registration
        );

        return pending.Task;
    }
    /// <inheritdoc/>
    public void Release(int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: count, other: 1);
        Write(line: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Prefix}release {count}"));
    }

    private void Read(TextReader answers) {
        while (true) {
            string? line;

            try {
                line = answers.ReadLine();
            } catch (Exception exception) when ((exception is IOException or ObjectDisposedException)) {
                line = null;
            }
            if (line is null) {
                End();

                return;
            }

            TaskCompletionSource<int>? pending;

            lock (m_gate) {
                _ = m_pending.TryDequeue(result: out pending);
            }
            if (
                (pending is null) ||
                !line.StartsWith(comparisonType: StringComparison.Ordinal, value: Granted) ||
                !int.TryParse(provider: CultureInfo.InvariantCulture, result: out var granted, s: line[Granted.Length..], style: NumberStyles.None) ||
                (granted < 1)
            ) {
                _ = pending?.TrySetException(exception: new IOException(message: $"The build host answered a core request with '{line}'."));
                End();

                return;
            }
            if (!pending.TrySetResult(result: granted)) {
                Release(count: granted);
            }
        }
    }
    private void End() {
        TaskCompletionSource<int>[] pending;

        lock (m_gate) {
            m_ended = true;
            pending = [.. m_pending];
            m_pending.Clear();
        }
        foreach (var request in pending) {
            _ = request.TrySetException(exception: new IOException(message: "The build host stopped answering core requests."));
        }
    }
    private void Write(string line) {
        lock (m_requests) {
            m_requests.WriteLine(value: line);
            m_requests.Flush();
        }
    }
}
