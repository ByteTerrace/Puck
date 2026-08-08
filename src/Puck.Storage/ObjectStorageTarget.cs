namespace Puck.Storage;

public abstract record ObjectStorageTarget {
    /// <summary>Owns backend target null and subtype validation at the storage dispatch boundary.</summary>
    internal static TTarget Require<TTarget>(ObjectStorageTarget target, string description)
        where TTarget : ObjectStorageTarget {
        ArgumentNullException.ThrowIfNull(target);

        return ((target as TTarget)
            ?? throw new ArgumentException(
                message: $"The storage target must be {description}.",
                paramName: nameof(target)));
    }
}
