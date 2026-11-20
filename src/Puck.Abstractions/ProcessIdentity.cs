using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.Abstractions;

/// <summary>The user and group the current process runs as, as the Linux kernel names them: the identity that owns the
/// files the process creates and that every permission check on them is made against.</summary>
public static partial class ProcessIdentity {
    /// <summary>Gets the process's effective user id.</summary>
    [SupportedOSPlatform("linux")]
    public static uint LinuxUserId => GetEuid();
    /// <summary>Gets the process's effective group id.</summary>
    [SupportedOSPlatform("linux")]
    public static uint LinuxGroupId => GetEgid();

    /// <summary>Returns the effective user and group ids in the <c>uid:gid</c> form <c>chown</c> and <c>docker run --user</c>
    /// take.</summary>
    /// <returns>The pair, or <see langword="null"/> on a host that is not Linux.</returns>
    public static string? LinuxUserAndGroup() => (OperatingSystem.IsLinux() ? $"{LinuxUserId}:{LinuxGroupId}" : null);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();
    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint GetEgid();
}
