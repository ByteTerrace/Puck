using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Puck.Networking;

/// <summary>Applies OS-user ownership and access policy to local-control files and artifact directories.</summary>
public static partial class LocalUserAccess {
    /// <summary>Builds a protected ACL owned by and accessible only to the current Windows user.</summary>
    /// <typeparam name="TSecurity">FileSecurity or DirectorySecurity, matching the object being created.</typeparam>
    /// <returns>The ACL to apply atomically when creating the object.</returns>
    /// <exception cref="UnauthorizedAccessException">The current identity has no Windows SID.</exception>
    [SupportedOSPlatform("windows")]
    public static TSecurity Create<TSecurity>() where TSecurity : FileSystemSecurity, new() {
        using var identity = WindowsIdentity.GetCurrent();
        var user = (identity.User ?? throw new UnauthorizedAccessException(message: "The current user has no Windows SID."));
        var security = new TSecurity();

        security.SetOwner(identity: user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = ((security is DirectorySecurity) ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None);

        security.AddAccessRule(rule: new FileSystemAccessRule(fileSystemRights: FileSystemRights.FullControl, identity: user, inheritanceFlags: inheritance, propagationFlags: PropagationFlags.None, type: AccessControlType.Allow));
        return security;
    }
}
