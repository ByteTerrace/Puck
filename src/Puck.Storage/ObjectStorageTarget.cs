namespace Puck.Storage;

/// <summary>
/// Where a routed blob store reads and writes: a storage provider's own target. Each target names the backend that
/// serves it, so a provider adds a store by adding a target and its backend in its own project, and the routed store
/// (<see cref="IObjectBlobStore"/>) dispatches through <see cref="Backend"/> without a registration step a new provider
/// could forget. The local directory target lives here; a cloud provider's target lives in that provider's project.
/// </summary>
public abstract record ObjectStorageTarget {
    /// <summary>Gets the backend that serves this target's provider. Only the routed store calls it.</summary>
    protected internal abstract IObjectBlobStoreBackend Backend { get; }
    /// <summary>Gets a one-line, secret-free description of this target for a status echo: what kind of store it is
    /// and how it is reached, never a credential or connection secret.</summary>
    public abstract string Description { get; }

    /// <summary>Returns <paramref name="target"/> as the backend's own target type, refusing any other.</summary>
    /// <typeparam name="TTarget">The target type the calling backend serves.</typeparam>
    /// <param name="target">The target handed to the backend.</param>
    /// <param name="description">The target type's name for the refusal message.</param>
    /// <returns>The typed target.</returns>
    /// <exception cref="ArgumentException"><paramref name="target"/> is another provider's target.</exception>
    public static TTarget Require<TTarget>(ObjectStorageTarget target, string description)
        where TTarget : ObjectStorageTarget {
        ArgumentNullException.ThrowIfNull(target);

        return ((target as TTarget)
            ?? throw new ArgumentException(
            message: $"The storage target must be {description}.",
            paramName: nameof(target)
        ));
    }
}
