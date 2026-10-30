using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Puck.Confinement;

/// <summary>The names an install root holds, shared by the stub, every entry point that can run from an install, and
/// the launcher that installs and prunes versions.</summary>
public static class InstallLayout {
    /// <summary>The current-version pointer file's name, directly under the install root. Its presence marks a directory
    /// as an install root.</summary>
    public const string CurrentFileName = "current";
    /// <summary>Where a version waits for deletion after leaving <see cref="VersionsDirectoryName"/>.</summary>
    public const string TrashDirectoryName = "trash";
    /// <summary>The update lease's file name, directly under the install root.</summary>
    public const string UpdateLockFileName = "update.lock";
    /// <summary>The version directories' parent, directly under the install root.</summary>
    public const string VersionsDirectoryName = "versions";
}
/// <summary>A launch that cannot confirm the version directory it runs from: the launcher removed or replaced it
/// before this process registered, or its install cannot be read, so running on would read files that are not this
/// version's.</summary>
/// <param name="message">The reason, naming the version.</param>
public sealed class InstallRegistrationException(string message) : IOException(message: message);
/// <summary>
/// A process's registration as a holder of the version directory it runs from. The launcher removes a version only
/// while no live process holds it, and a held directory is a handle the operating system closes when the process exits,
/// a crash included (<see cref="ConfinedDirectory.TryHoldShared"/>).
/// <para>Every Puck entry point that can run from an install's <c>versions/&lt;version&gt;/</c> calls
/// <see cref="TryRegisterProcess"/> before it does anything else and refuses to start when it fails. The stub also holds
/// its child's version from before the child starts.</para>
/// <para>Registration waits for <see cref="InstallLayout.UpdateLockFileName"/> without a deadline, the lease every
/// removal holds, and then confirms by identity that <c>versions/&lt;name&gt;</c> is still the directory this process was
/// loaded from: the loaded image's file must be the very file that names it there now. Whether the process runs from the
/// install at all is decided by the identity of the image's parent directory, never by the spelling of a path.</para>
/// </summary>
public sealed class InstallRegistration : IDisposable {
    private static readonly Lock ProcessGate = new();

    private readonly ConfinedDirectory? m_held;

    private InstallRegistration(ConfinedDirectory? held, string? version, ConfinedIdentity? root) {
        m_held = held;
        Root = root;
        Version = version;
    }

    /// <summary>Gets this process's own registration once <see cref="TryRegisterProcess"/> or
    /// <see cref="RegisterProcess"/> has run, or <see langword="null"/> before.</summary>
    public static InstallRegistration? Process { get; private set; }
    /// <summary>Gets the identity of the install root this registration holds a version of, or <see langword="null"/>
    /// when it holds none.</summary>
    public ConfinedIdentity? Root { get; }
    /// <summary>Gets the name of the version directory this registration holds, or <see langword="null"/> when the
    /// process does not run from an install root's <c>versions/</c>.</summary>
    public string? Version { get; }

    /// <summary>Registers a process whose image file is <paramref name="imagePath"/> against
    /// <paramref name="installRoot"/>.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="imagePath">The path the process's image (its entry assembly) was loaded from.</param>
    /// <param name="imageAddress">An address inside the image's mapping on Windows; ignored on Linux.</param>
    /// <param name="cancellationToken">Ends the wait for the update lease.</param>
    /// <returns>The registration, held until disposed; its <see cref="Version"/> is <see langword="null"/> for a process
    /// outside the install.</returns>
    /// <exception cref="InstallRegistrationException">The process runs from the install, but its version directory was
    /// removed, replaced or is being removed.</exception>
    /// <exception cref="IOException">The install root or the update lease cannot be opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired while waiting.</exception>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public static InstallRegistration Register(string installRoot, string imagePath, nint imageAddress, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: imagePath);
        var image = Path.GetFullPath(path: imagePath);
        var imageDirectory = Path.GetDirectoryName(path: image);
        var imageParent = ((imageDirectory is null) ? null : Path.GetDirectoryName(path: imageDirectory));
        using var root = ConfinedDirectory.OpenAnchor(create: false, path: installRoot);

        if ((root is null) || (imageParent is null)) { return None; }
        using var lease = root.AcquireLock(cancellationToken: cancellationToken, name: InstallLayout.UpdateLockFileName, timeout: Timeout.InfiniteTimeSpan);
        using var versions = root.Child(name: InstallLayout.VersionsDirectoryName);
        using var trash = root.Child(name: InstallLayout.TrashDirectoryName);
        using var parent = ConfinedDirectory.OpenAnchor(create: false, path: imageParent);
        var name = Path.GetFileName(path: imageDirectory!);

        if ((parent is null) || (versions is null) || (parent.Identity != versions.Identity)) {
            return (((parent is not null) && (trash is not null) && (parent.Identity == trash.Identity))
                ? throw new InstallRegistrationException(message: $"this process runs from a version the launcher removed ('{imageDirectory}'); relaunch through the stub.")
                : None);
        }
        ConfinedDirectory? held;

        try {
            held = versions.Child(name: name);
        } catch (IOException error) {
            throw new InstallRegistrationException(message: $"versions/{name} cannot be held: {error.Message}");
        }
        if (held is null) {
            throw new InstallRegistrationException(message: $"versions/{name} was removed before this process registered; relaunch through the stub.");
        }
        try {
            if (!held.IsMapped(address: imageAddress, name: Path.GetFileName(path: image))) {
                throw new InstallRegistrationException(message: $"versions/{name} is no longer the directory this process was loaded from; relaunch through the stub.");
            }
            if (!held.TryHoldShared()) {
                throw new InstallRegistrationException(message: $"versions/{name} is being removed; relaunch through the stub.");
            }
            return new InstallRegistration(held: held, root: root.Identity, version: name);
        } catch {
            held.Dispose();
            throw;
        }
    }
    /// <summary>Registers this process once, by its entry assembly, and keeps the registration for the life of the
    /// process (<see cref="Process"/>). A process whose image directory's grandparent is an install root (it holds
    /// <see cref="InstallLayout.CurrentFileName"/>) registers against it; any other process registers as nothing.</summary>
    /// <param name="cancellationToken">Ends the wait for the update lease.</param>
    /// <returns>This process's registration.</returns>
    /// <exception cref="InstallRegistrationException">The process runs from an install, but its version directory was
    /// removed, replaced or is being removed.</exception>
    /// <exception cref="IOException">The install root or the update lease cannot be opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired while waiting.</exception>
    [UnconditionalSuppressMessage(category: "SingleFile", checkId: "IL3000", Justification = "A single-file app has an empty entry Location, which registers as nothing before any file API is reached.")]
    [UnconditionalSuppressMessage(category: "SingleFile", checkId: "IL3002", Justification = "GetHINSTANCE is reached only for an entry assembly loaded from a file, after the empty-Location check.")]
    public static InstallRegistration RegisterProcess(CancellationToken cancellationToken) {
        lock (ProcessGate) {
            if (Process is { } registered) { return registered; }
            if ((Assembly.GetEntryAssembly() is not { Location: { Length: > 0 } location } entry) ||
                (Path.GetDirectoryName(path: Path.GetDirectoryName(path: Path.GetDirectoryName(path: Path.GetFullPath(path: location)))) is not { } candidate) ||
                !File.Exists(path: Path.Combine(path1: candidate, path2: InstallLayout.CurrentFileName))) {
                return (Process = None);
            }
            var address = (OperatingSystem.IsWindows() ? Marshal.GetHINSTANCE(m: entry.ManifestModule) : 0);

            try {
                return (Process = Register(cancellationToken: cancellationToken, imageAddress: address, imagePath: location, installRoot: candidate));
            } catch (PlatformNotSupportedException) {
                // The launcher refuses to prune on this platform too, so nothing can remove the version this runs from.
                return (Process = None);
            }
        }
    }
    /// <summary>Registers this process (<see cref="RegisterProcess"/>) for an entry point that refuses to start when it
    /// cannot.</summary>
    /// <param name="refusal">The reason the process must not start, or <see langword="null"/> when it registered.</param>
    /// <returns><see langword="true"/> when the process registered, as a holder of its version or as nothing.</returns>
    public static bool TryRegisterProcess(out string? refusal) {
        try {
            _ = RegisterProcess(cancellationToken: CancellationToken.None);
            refusal = null;
            return true;
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            refusal = $"[install: refusing to start — {error.Message}]";
            return false;
        }
    }
    /// <summary>Releases the held version directory.</summary>
    public void Dispose() => m_held?.Dispose();

    private static InstallRegistration None => new(held: null, root: null, version: null);
}
