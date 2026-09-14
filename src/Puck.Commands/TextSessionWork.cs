namespace Puck.Commands;

internal abstract class TextSessionWork {
    public virtual bool IsTerminal => false;
    public virtual string? Line => null;

    public virtual void Execute(Func<IDisposable>? scope = null) { }
    public virtual void Refuse(Exception exception) { }
}
internal sealed class TextSessionLine(string line) : TextSessionWork {
    public override string Line => line;
}
internal sealed class TextSessionOperation<TResult> : TextSessionWork {
    private readonly TaskCompletionSource<TResult> m_completion = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<TResult> m_operation;
    private readonly CancellationTokenRegistration m_registration;

    private int m_state;

    public TextSessionOperation(Func<TResult> operation, CancellationToken cancellationToken) {
        m_operation = operation;
        m_registration = cancellationToken.Register(callback: () => {
            if (Interlocked.CompareExchange(
                comparand: 0,
                location1: ref m_state,
                value: 2
            ) == 0) {
                m_completion.TrySetCanceled(cancellationToken: cancellationToken);
            }
        });
    }

    public override bool IsTerminal => (Volatile.Read(location: ref m_state) == 2);
    public Task<TResult> Task => m_completion.Task;

    public override void Execute(Func<IDisposable>? scope = null) {
        if (Interlocked.CompareExchange(
            comparand: 0,
            location1: ref m_state,
            value: 1
        ) != 0) {
            m_registration.Dispose();
            return;
        }

        try {
            TResult result;

            using (scope?.Invoke()) {
                result = m_operation();
            }
            m_completion.TrySetResult(result: result);
        } catch (Exception exception) {
            m_completion.TrySetException(exception: exception);
        } finally {
            Volatile.Write(
                location: ref m_state,
                value: 2
            );
            m_registration.Dispose();
        }
    }
    public override void Refuse(Exception exception) {
        if (Interlocked.CompareExchange(
            comparand: 0,
            location1: ref m_state,
            value: 2
        ) == 0) {
            m_completion.TrySetException(exception: exception);
        }

        m_registration.Dispose();
    }
}
