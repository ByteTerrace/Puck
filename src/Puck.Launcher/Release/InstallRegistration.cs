using System.Reflection;
using System.Runtime.InteropServices;
using Puck.Confinement;

namespace Puck.Launcher.Release;

/// <summary>A launch that cannot confirm the version directory it runs from: the launcher removed or replaced it
/// before this process registered, so running on would read files that are not this version's.</summary>
/// <param name="message">The reason, naming the version.</param>
public sealed class InstallRegistrationException(string message) : IOException(message: message);

/// <summary>
/// This process's registration as a holder of the version directory it runs from. A version is deletable only while no
/// live process holds it (<see cref="InstallPruner"/>), and a held directory is a handle the operating system closes
/// when the process exits, a crash included (<see cref="ConfinedDirectory.TryHoldShared"/>). The stub holds its child's
/// version from before the child starts; <see cref="Register"/> covers every process, a direct launch from
/// <c>versions/&lt;version&gt;/</c> included.
/// <para>Registration waits for <see cref="InstallLeases.UpdateLockFileName"/> without a deadline, the lease every
/// removal holds, and then confirms by identity that <c>versions/&lt;name&gt;</c> is still the directory this process
/// was loaded from: the loaded image's file must be the very file that names it there now. A removed or replaced
/// directory fails the launch with <see cref="InstallRegistrationException"/>. Whether the process runs from the install
/// at all is decided by the identity of the image's parent directory, never by the spelling of a path.</para>
/// </summary>
public sealed class InstallRegistration : IDisposable {
    private readonly ConfinedDirectory? m_held;

    private InstallRegistration(ConfinedDirectory? held, string? version) {
        m_held = held;
        Version = version;
    }

    /// <summary>Gets the name of the version directory this registration holds, or <see langword="null"/> when the
    /// process does not run from the install root's <c>versions/</c>.</summary>
    public string? Version { get; }

    /// <summary>Registers a process whose image file is <paramref name="imagePath"/>.</summary>
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
    public static InstallRegistration Register(string installRoot, string imagePath, nint imageAddress, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: imagePath);
        var image = Path.GetFullPath(path: imagePath);
        var imageDirectory = Path.GetDirectoryName(path: image);
        var imageParent = ((imageDirectory is null) ? null : Path.GetDirectoryName(path: imageDirectory));
        using var root = ConfinedDirectory.OpenAnchor(create: false, path: installRoot);

        if ((root is null) || (imageParent is null)) { return new InstallRegistration(held: null, version: null); }
        using var lease = root.AcquireLock(cancellationToken: cancellationToken, name: InstallLeases.UpdateLockFileName, timeout: Timeout.InfiniteTimeSpan);
        using var versions = root.Child(name: InstallPruner.VersionsDirectoryName);
        using var trash = root.Child(name: InstallPruner.TrashDirectoryName);
        using var parent = ConfinedDirectory.OpenAnchor(create: false, path: imageParent);
        var name = Path.GetFileName(path: imageDirectory!);

        if ((parent is null) || (versions is null) || (parent.Identity != versions.Identity)) {
            return (((parent is not null) && (trash is not null) && (parent.Identity == trash.Identity))
                ? throw new InstallRegistrationException(message: $"this process runs from a version the launcher removed ('{imageDirectory}'); relaunch through the stub.")
                : new InstallRegistration(held: null, version: null));
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
            return new InstallRegistration(held: held, version: name);
        } catch {
            held.Dispose();
            throw;
        }
    }
    /// <summary>Registers this process by its entry assembly.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="cancellationToken">Ends the wait for the update lease.</param>
    /// <returns>The registration; its <see cref="Version"/> is <see langword="null"/> for a process outside the install,
    /// one without an entry assembly file, or one on a platform <see cref="ConfinedDirectory"/> does not support.</returns>
    /// <exception cref="InstallRegistrationException">The process runs from the install, but its version directory was
    /// removed, replaced or is being removed.</exception>
    /// <exception cref="IOException">The install root or the update lease cannot be opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired while waiting.</exception>
    public static InstallRegistration RegisterEntryAssembly(string installRoot, CancellationToken cancellationToken) {
        if (Assembly.GetEntryAssembly() is not { Location: { Length: > 0 } location } entry) {
            return new InstallRegistration(held: null, version: null);
        }
        var address = (OperatingSystem.IsWindows() ? Marshal.GetHINSTANCE(m: entry.ManifestModule) : 0);

        try {
            return Register(cancellationToken: cancellationToken, imageAddress: address, imagePath: location, installRoot: installRoot);
        } catch (PlatformNotSupportedException) {
            // Pruning refuses on this platform too, so nothing can remove the version this process runs from.
            return new InstallRegistration(held: null, version: null);
        }
    }

    /// <summary>Releases the held version directory.</summary>
    public void Dispose() => m_held?.Dispose();
}
