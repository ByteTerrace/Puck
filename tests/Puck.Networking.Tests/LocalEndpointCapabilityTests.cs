using System.Security.AccessControl;
using Xunit;

namespace Puck.Networking.Tests;

public sealed class LocalEndpointCapabilityTests {
    [Fact]
    public void LinuxCapabilityChecksTheOpenedPrivateInode() {
        if (!OperatingSystem.IsLinux()) { Assert.Skip(reason: "Linux inode checks require Linux."); return; }
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-linux-capability-{Guid.NewGuid():N}"
        );
        var link = (path + ".link");

        try {
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
        } finally { File.Delete(path: link); File.Delete(path: path); }
    }
    [Fact]
    public void NullDaclIsNotMistakenForAnOwnerOnlyCapability() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-acl-test-{Guid.NewGuid():N}.json"
        );

        try {
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
        } finally { File.Delete(path: path); }
    }
}
