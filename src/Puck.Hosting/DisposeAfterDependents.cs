namespace Puck.Hosting;

/// <summary>
/// Owns a resource that other objects were made from and must not outlive, such as a device and the images created on
/// it: the resource is disposed once its owner has retired it and every dependent has been released, in whichever
/// order the two happen. A dependent's own release can be deferred (an image still sampled by a submitted frame), so
/// the owner retires the resource without knowing when that release comes. Single-threaded: every call runs on the
/// thread that owns the dependents.
/// </summary>
/// <typeparam name="TResource">The resource type.</typeparam>
public sealed class DisposeAfterDependents<TResource> where TResource : class, IDisposable {
    private int m_dependents;

    /// <summary>Initializes a new instance of the <see cref="DisposeAfterDependents{TResource}"/> class.</summary>
    /// <param name="resource">The resource to own; this instance disposes it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    public DisposeAfterDependents(TResource resource) {
        ArgumentNullException.ThrowIfNull(argument: resource);

        Resource = resource;
    }

    /// <summary>Gets the number of dependents not yet released.</summary>
    public int Dependents => m_dependents;
    /// <summary>Gets whether the resource has been disposed.</summary>
    public bool IsDisposed { get; private set; }
    /// <summary>Gets whether the owner has retired the resource.</summary>
    public bool IsRetired { get; private set; }
    /// <summary>Gets the owned resource; it stays readable after disposal.</summary>
    public TResource Resource { get; }

    private void DisposeWhenFree() {
        if (
            IsRetired &&
            (0 == m_dependents) &&
            !IsDisposed
        ) {
            IsDisposed = true;
            Resource.Dispose();
        }
    }

    /// <summary>Records one dependent made from the resource.</summary>
    /// <exception cref="InvalidOperationException">The resource is already retired, so nothing new may be made from
    /// it.</exception>
    public void AddDependent() {
        if (IsRetired) {
            throw new InvalidOperationException(message: $"A {typeof(TResource).Name} was retired, so nothing new may depend on it.");
        }

        ++m_dependents;
    }
    /// <summary>Records the release of one dependent, and disposes the resource when it is retired and this was the
    /// last one.</summary>
    /// <exception cref="InvalidOperationException">No dependent is outstanding.</exception>
    public void RemoveDependent() {
        if (0 == m_dependents) {
            throw new InvalidOperationException(message: $"A {typeof(TResource).Name} dependent was released more times than one was added.");
        }

        --m_dependents;
        DisposeWhenFree();
    }
    /// <summary>Retires the resource: it is disposed now when no dependent is outstanding, or by the last
    /// <see cref="RemoveDependent"/> otherwise. Safe to call more than once.</summary>
    public void Retire() {
        IsRetired = true;
        DisposeWhenFree();
    }
}
