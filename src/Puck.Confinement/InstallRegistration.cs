using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Puck.Confinement;

/// <summary>The names an install root holds, shared by the stub, every entry point that can run from an install, and
/// the launcher that installs and prunes versions.</summary>
public static class InstallLayout {
    /// <summary>The pending install intent's file name, directly under the install root.</summary>
    public const string InstallIntentFileName = "install-intent.json";
    /// <summary>The install's release record's file name (<see cref="ReleasePolicy"/>), under
    /// <see cref="StateDirectoryName"/>.</summary>
    public const string ReleasePolicyFileName = "release-policy.json";
    /// <summary>The install's release replay mark's file name, under <see cref="StateDirectoryName"/>. It is read and
    /// written only under an <see cref="InstallTransaction"/>.</summary>
    public const string SequenceMarkFileName = "sequence-mark";
    /// <summary>The install's durable state directory, directly under the install root.</summary>
    public const string StateDirectoryName = "state";
    /// <summary>Where a version waits for deletion after leaving <see cref="VersionsDirectoryName"/>.</summary>
    public const string TrashDirectoryName = "trash";
    /// <summary>The update lease's file name, directly under the install root. <see cref="InstallTransaction"/> holds it.</summary>
    public const string UpdateLockFileName = "update.lock";
    /// <summary>The version directories' parent, directly under the install root.</summary>
    public const string VersionsDirectoryName = "versions";

    /// <summary>Reports whether <paramref name="name"/> can name a version directory: one non-empty component that is
    /// not <c>.</c> or <c>..</c> and holds no separator, drive colon or NUL.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns><see langword="true"/> when the name is a single plain component.</returns>
    public static bool IsPlainName(string? name) =>
        (!string.IsNullOrEmpty(value: name) && (name is not ("." or "..")) && (name.IndexOfAny(anyOf: ['/', '\\', ':', '\0']) < 0));
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
/// install at all is decided by the identity of the image's parent directory, never by the spelling of a path. Under the same
/// lease the version must then pass the release record's admission (<see cref="InstallTransaction.AdmitProcess"/>): a version
/// revoked, rejected or below the state generation floor refuses to start, whoever started it.</para>
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
            // Every installed entry point passes here first, so this is the launch policy's door for every start, the stub's
            // children included: a version revoked, rejected or below the floor since the stub chose it does not run.
            string? refusal;

            try {
                using var transaction = InstallTransaction.Under(root: root);

                refusal = transaction.AdmitProcess(version: name);
            } catch (InvalidDataException error) {
                refusal = error.Message;
            }
            if (refusal is not null) {
                throw new InstallRegistrationException(message: $"versions/{name} may not run: {refusal}.");
            }
            return new InstallRegistration(held: held, root: root.Identity, version: name);
        } catch {
            held.Dispose();
            throw;
        }
    }
    /// <summary>Registers a process by where it runs from. Membership is decided by the process's own directory,
    /// <paramref name="baseDirectory"/>: a directory directly under one named <see cref="InstallLayout.VersionsDirectoryName"/>
    /// (or <see cref="InstallLayout.TrashDirectoryName"/>) belongs to the install root above it, whether or not that root
    /// has a release record yet, and registers against it. Any other directory
    /// registers as nothing. The image whose identity is confirmed is the entry assembly's file, or the process's own
    /// executable for a single-file app.</summary>
    /// <param name="baseDirectory">The process's own directory (<see cref="AppContext.BaseDirectory"/>).</param>
    /// <param name="entryAssemblyLocation">The entry assembly's file, or <see langword="null"/> or empty for a single-file
    /// app.</param>
    /// <param name="processPath">The process's executable (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="imageAddress">An address inside the image's mapping on Windows; ignored on Linux.</param>
    /// <param name="cancellationToken">Ends the wait for the update lease.</param>
    /// <returns>The registration; its <see cref="Version"/> is <see langword="null"/> for a process outside any install.</returns>
    /// <exception cref="InstallRegistrationException">The process sits under an install's <c>versions/</c> but its image
    /// or install root cannot be confirmed, or its version directory was removed, replaced or is being removed.</exception>
    /// <exception cref="IOException">The install root or the update lease cannot be opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired while waiting.</exception>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public static InstallRegistration RegisterImage(string baseDirectory, string? entryAssemblyLocation, string? processPath, nint imageAddress, CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: baseDirectory);
        var comparison = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var directory = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: baseDirectory));
        var parent = Path.GetDirectoryName(path: directory);
        var parentName = ((parent is null) ? "" : Path.GetFileName(path: parent));

        if (!parentName.Equals(comparisonType: comparison, value: InstallLayout.VersionsDirectoryName) &&
            !parentName.Equals(comparisonType: comparison, value: InstallLayout.TrashDirectoryName)) {
            return None;
        }
        var image = (string.IsNullOrEmpty(value: entryAssemblyLocation) ? processPath : entryAssemblyLocation);

        if (string.IsNullOrEmpty(value: image) || !string.Equals(a: Path.GetDirectoryName(path: Path.GetFullPath(path: image)), b: directory, comparisonType: comparison)) {
            throw new InstallRegistrationException(message: $"'{directory}' sits under an install's {parentName}/, but the image this process was loaded from is not in it; relaunch through the stub.");
        }
        var registration = Register(cancellationToken: cancellationToken, imageAddress: imageAddress, imagePath: image, installRoot: Path.GetDirectoryName(path: parent!)!);

        return ((registration.Version is not null)
            ? registration
            : throw new InstallRegistrationException(message: $"'{directory}' sits under an install's {parentName}/, but its install root cannot be confirmed; relaunch through the stub."));
    }
    /// <summary>Registers this process once (<see cref="RegisterImage"/> over its own directory, entry assembly and
    /// executable) and keeps the registration for the life of the process (<see cref="Process"/>).</summary>
    /// <param name="cancellationToken">Ends the wait for the update lease.</param>
    /// <returns>This process's registration.</returns>
    /// <exception cref="InstallRegistrationException">The process sits under an install's <c>versions/</c> but cannot
    /// confirm it, or its version directory was removed, replaced or is being removed.</exception>
    /// <exception cref="IOException">The install root or the update lease cannot be opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired while waiting.</exception>
    [UnconditionalSuppressMessage(category: "SingleFile", checkId: "IL3000", Justification = "A single-file app's empty entry Location selects its executable, which is the image it was loaded from.")]
    [UnconditionalSuppressMessage(category: "SingleFile", checkId: "IL3002", Justification = "GetHINSTANCE is reached only for an entry assembly loaded from a file; a single-file app uses its executable's module.")]
    public static InstallRegistration RegisterProcess(CancellationToken cancellationToken) {
        lock (ProcessGate) {
            if (Process is { } registered) { return registered; }
            var entry = Assembly.GetEntryAssembly();
            var location = entry?.Location;
            var address = ((!OperatingSystem.IsWindows())
                ? 0
                : (string.IsNullOrEmpty(value: location) ? ConfinedDirectory.ProcessImageAddress() : Marshal.GetHINSTANCE(m: entry!.ManifestModule)));

            try {
                return (Process = RegisterImage(baseDirectory: AppContext.BaseDirectory, cancellationToken: cancellationToken, entryAssemblyLocation: location,
                    imageAddress: address, processPath: Environment.ProcessPath));
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
