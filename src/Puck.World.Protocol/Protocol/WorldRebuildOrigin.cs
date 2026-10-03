namespace Puck.World.Protocol;

/// <summary>Where a load or reload's document came from. The producer states the case; nothing reads it back from a
/// spelling, because a relative file path and a store identity can look alike.</summary>
public abstract record WorldRebuildOrigin {
    private protected WorldRebuildOrigin() { }

    /// <summary>A document file, as <c>world.load</c> and <c>world.reload</c> read it.</summary>
    /// <param name="Path">The file the rebuild read, as the reader named it.</param>
    public sealed record File(string Path) : WorldRebuildOrigin {
        /// <summary>Returns the file's path.</summary>
        /// <returns>The path.</returns>
        public override string ToString() => Path;
    }
    /// <summary>A hosted world's document in its owner's store, as a silo's reload reads it.</summary>
    /// <param name="Owner">The owning identity's oid.</param>
    /// <param name="World">The world id under that owner's container.</param>
    public sealed record Store(Guid Owner, SafeName World) : WorldRebuildOrigin {
        /// <summary>Returns the store identity, <c>owner/{oid}/{world}</c>.</summary>
        /// <returns>The identity.</returns>
        public override string ToString() => $"owner/{Owner:D}/{World.Value}";
    }
}
