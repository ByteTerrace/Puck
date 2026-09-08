namespace Puck.Commands;

internal abstract class TextSessionWork {
    public virtual string? Line => null;
    public virtual bool IsTerminal => false;
    public virtual void Execute(Func<IDisposable>? scope = null) { }
    public virtual void Refuse(Exception exception) { }
}

internal sealed class TextSessionLine(string line) : TextSessionWork {
    public override string Line => line;
}

internal sealed class TextSessionOperation<TResult> : TextSessionWork {
    private readonly Func<TResult> m_operation;
    private readonly TaskCompletionSource<TResult> m_completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration m_registration;
    private int m_state;

    public TextSessionOperation(Func<TResult> operation, CancellationToken cancellationToken) {
        m_operation = operation;
        m_registration = cancellationToken.Register(() => {
            if (Interlocked.CompareExchange(ref m_state, 2, 0) == 0) {
                m_completion.TrySetCanceled(cancellationToken);
            }
        });
    }

    public Task<TResult> Task => m_completion.Task;
    public override bool IsTerminal => Volatile.Read(ref m_state) == 2;

    public override void Execute(Func<IDisposable>? scope = null) {
        if (Interlocked.CompareExchange(ref m_state, 1, 0) != 0) {
            m_registration.Dispose();
            return;
        }

        try {
            TResult result;
            using (scope?.Invoke()) {
                result = m_operation();
            }
            m_completion.TrySetResult(result);
        } catch (Exception exception) {
            m_completion.TrySetException(exception);
        } finally {
            Volatile.Write(ref m_state, 2);
            m_registration.Dispose();
        }
    }

    public override void Refuse(Exception exception) {
        if (Interlocked.CompareExchange(ref m_state, 2, 0) == 0) {
            m_completion.TrySetException(exception);
        }

        m_registration.Dispose();
    }
}
