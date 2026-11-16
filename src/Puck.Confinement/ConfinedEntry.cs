namespace Puck.Confinement;

/// <summary>What a directory entry was when its directory was listed. A listing is a hint only: every operation on
/// the entry opens it again relative to its directory's handle and checks that handle.</summary>
public enum ConfinedEntryKind {
    /// <summary>A regular file.</summary>
    File,
    /// <summary>A directory that is not a link.</summary>
    Directory,
    /// <summary>A symbolic link, junction or any other reparse point.</summary>
    Link,
    /// <summary>A device, pipe, socket or another kind of entry.</summary>
    Other,
}
/// <summary>One entry of a <see cref="ConfinedDirectory"/> listing, as the listing read it from the entry itself and
/// never from what a link names.</summary>
/// <param name="Name">The entry's name within its directory.</param>
/// <param name="Kind">What the entry was when the directory was listed.</param>
/// <param name="Length">The entry's length in bytes when listed; meaningful for a regular file.</param>
/// <param name="LastWriteTimeUtc">The entry's last write time, in UTC, when listed.</param>
public readonly record struct ConfinedEntry(string Name, ConfinedEntryKind Kind, long Length, DateTime LastWriteTimeUtc);
/// <summary>One entry below a <see cref="ConfinedDirectory"/> that <see cref="ConfinedDirectory.Walk"/> reached.</summary>
/// <param name="Path">The entry's path relative to the walked directory, with forward slashes.</param>
/// <param name="Entry">The entry as its directory listed it.</param>
/// <param name="Directory">The held directory the entry is in: the walked directory itself or one opened below it,
/// through handles. It is valid only until the walk moves on.</param>
public readonly record struct ConfinedTreeEntry(string Path, ConfinedEntry Entry, ConfinedDirectory Directory);
/// <summary>The file system's identity of an open file or directory: equal identities name the same object, whatever
/// path spelling reached it.</summary>
/// <param name="Volume">The volume serial number on Windows, or the device number on Linux.</param>
/// <param name="File">The 128-bit file identifier on Windows, or the inode number on Linux.</param>
public readonly record struct ConfinedIdentity(ulong Volume, UInt128 File);
