using System.Security.AccessControl;
using Puck.Testing;
using Xunit;

namespace Puck.Networking.Tests;

public sealed class LocalEndpointCapabilityTests {
    [Fact]
    public void LinuxCapabilityChecksTheOpenedPrivateInode() {
        if (!OperatingSystem.IsLinux()) { Assert.Skip(reason: "Linux inode checks require Linux."); return; }
        using var directory = new TemporaryDirectory(prefix: "puck-linux-capability-");

        var path = directory.PathOf(name: "capability");
        var link = directory.PathOf(name: "capability.link");

        var capability = new LocalEndpointCapability(port: 12345);

        using (capability.WriteDescriptor(path: path)) { }
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path: path)
        );
        Assert.Equal(
            12345,
            LocalEndpointCapability.ReadDescriptor(path: path).Port
        );
        File.CreateSymbolicLink(
            path: link,
            pathToTarget: path
        );
        Assert.Throws<IOException>(testCode: () => LocalEndpointCapability.ReadDescriptor(path: link));
        File.SetUnixFileMode(
            mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            path: path
        );
        Assert.Throws<UnauthorizedAccessException>(testCode: () => LocalEndpointCapability.ReadDescriptor(path: path));
    }
    [Fact]
    public void NullDaclIsNotMistakenForAnOwnerOnlyCapability() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var directory = new TemporaryDirectory(prefix: "puck-acl-test-");

        var path = directory.PathOf(name: "capability.json");

        var capability = new LocalEndpointCapability(port: 12345);

        using (capability.WriteDescriptor(path: path)) { }
        var file = new FileInfo(fileName: path);
        var acl = file.GetAccessControl();
        var descriptor = new RawSecurityDescriptor(
            binaryForm: acl.GetSecurityDescriptorBinaryForm(),
            offset: 0
        ) { DiscretionaryAcl = null };
        var bytes = new byte[descriptor.BinaryLength];

        descriptor.GetBinaryForm(
            binaryForm: bytes,
            offset: 0
        );
        acl.SetSecurityDescriptorBinaryForm(
            binaryForm: bytes,
            includeSections: AccessControlSections.Access
        );
        file.SetAccessControl(fileSecurity: acl);
        Assert.Null(@object: new RawSecurityDescriptor(
            binaryForm: file.GetAccessControl().GetSecurityDescriptorBinaryForm(),
            offset: 0
        ).DiscretionaryAcl);
        Assert.Throws<UnauthorizedAccessException>(testCode: () => LocalEndpointCapability.ReadDescriptor(path: path));
    }
}
