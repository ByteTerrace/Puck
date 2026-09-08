using System.Security.AccessControl;
using Xunit;

namespace Puck.Networking.Tests;

public sealed class LocalEndpointCapabilityTests {
    [Fact]
    public void LinuxCapabilityChecksTheOpenedPrivateInode() {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux inode checks require Linux."); return; }
        var path = Path.Combine(Path.GetTempPath(), $"puck-linux-capability-{Guid.NewGuid():N}");
        var link = path + ".link";
        try {
            var capability = new LocalEndpointCapability(12345);
            using (capability.WriteDescriptor(path)) { }
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(12345, LocalEndpointCapability.ReadDescriptor(path).Port);
            File.CreateSymbolicLink(link, path);
            Assert.Throws<IOException>(() => LocalEndpointCapability.ReadDescriptor(link));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<UnauthorizedAccessException>(() => LocalEndpointCapability.ReadDescriptor(path));
        } finally { File.Delete(link); File.Delete(path); }
    }
    [Fact]
    public void NullDaclIsNotMistakenForAnOwnerOnlyCapability() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        var path = Path.Combine(Path.GetTempPath(), $"puck-acl-test-{Guid.NewGuid():N}.json");
        try {
            var capability = new LocalEndpointCapability(12345);
            using (capability.WriteDescriptor(path)) { }
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            var descriptor = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0) { DiscretionaryAcl = null };
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access);
            file.SetAccessControl(acl);
            Assert.Null(new RawSecurityDescriptor(file.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl);
            Assert.Throws<UnauthorizedAccessException>(() => LocalEndpointCapability.ReadDescriptor(path));
        } finally { File.Delete(path); }
    }
}
