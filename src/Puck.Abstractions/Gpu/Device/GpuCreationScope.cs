using System.Runtime.ExceptionServices;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The objects an owner creates while it is being built. Each creation joins the scope as it is made
/// (<see cref="Own{T}"/>, or <see cref="Own(nint, Action{nint})"/> for a handle released through a device call), and the
/// build ends with <see cref="Complete"/>, which hands every one of them to the owner. A build that throws first leaves
/// the scope incomplete, and disposing it releases exactly what was created, newest first, so a creation that fails
/// partway leaks nothing. Declare it with <see langword="using"/> at the top of the build.
/// <para>
/// A release that throws does not stop the others; once every release has run, the first such failure is rethrown in
/// place of the build's own exception. A scope is used on the thread that builds, and is not reusable.
/// </para>
/// </summary>
public sealed class GpuCreationScope : IDisposable {
    private readonly List<(IDisposable? Disposable, nint Handle, Action<nint>? Release)> m_created = [];

    private bool m_completed;

    /// <summary>Gets how many creations the scope holds.</summary>
    public int Count => m_created.Count;

    /// <summary>Hands every creation to the owner: disposing the scope afterwards releases nothing.</summary>
    /// <exception cref="InvalidOperationException">The scope has already completed.</exception>
    public void Complete() {
        if (m_completed) {
            throw new InvalidOperationException(message: "The creation scope has already completed.");
        }

        m_completed = true;
        m_created.Clear();
    }
    /// <summary>Releases every creation, newest first, unless the scope has completed.</summary>
    public void Dispose() {
        if (m_completed) {
            return;
        }

        m_completed = true;

        ExceptionDispatchInfo? failure = null;

        for (var index = (m_created.Count - 1); (index >= 0); index--) {
            var (disposable, handle, release) = m_created[index];

            try {
                if (disposable is not null) {
                    disposable.Dispose();
                } else {
                    release!(obj: handle);
                }
            } catch (Exception exception) {
                failure ??= ExceptionDispatchInfo.Capture(source: exception);
            }
        }

        m_created.Clear();
        failure?.Throw();
    }
    /// <summary>Adds a created object to the scope and returns it.</summary>
    /// <typeparam name="T">The object's type.</typeparam>
    /// <param name="created">The object just created.</param>
    /// <returns><paramref name="created"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="created"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The scope has already completed or released.</exception>
    public T Own<T>(T created) where T : IDisposable {
        ArgumentNullException.ThrowIfNull(argument: created);
        ThrowIfEnded();
        m_created.Add(item: (created, 0, null));

        return created;
    }
    /// <summary>Adds a created handle to the scope, with the call that releases it, and returns it.</summary>
    /// <param name="handle">The handle just created: a descriptor pool or a sampler, for example.</param>
    /// <param name="release">The call that releases <paramref name="handle"/>.</param>
    /// <returns><paramref name="handle"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="release"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The scope has already completed or released.</exception>
    public nint Own(nint handle, Action<nint> release) {
        ArgumentNullException.ThrowIfNull(argument: release);
        ThrowIfEnded();
        m_created.Add(item: (null, handle, release));

        return handle;
    }

    private void ThrowIfEnded() {
        if (m_completed) {
            throw new InvalidOperationException(message: "The creation scope has already completed or released its creations.");
        }
    }
}
