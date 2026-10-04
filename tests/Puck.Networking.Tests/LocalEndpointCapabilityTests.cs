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
    // The descriptor names the shape of the capability's wire contract; the same revision under another shape is refused by
    // the reader before the port or secret is used.
    [Fact]
    public void ADescriptorCarriesTheLedgersShapeAndAnotherShapeIsRefused() {
        using var directory = new TemporaryDirectory(prefix: "puck-capability-shape-");

        var path = directory.PathOf(name: "capability");

        using (new LocalEndpointCapability(port: 12345).WriteDescriptor(path: path)) { }

        var recorded = FormatLedgerShapes.Of(id: "LocalEndpointCapability.Revision");
        var text = File.ReadAllText(path: path);

        Assert.Contains(
            actualString: text,
            expectedSubstring: $"\"shape\":\"{recorded}\""
        );
        Assert.Equal(
            12345,
            LocalEndpointCapability.ReadDescriptor(path: path).Port
        );
        File.WriteAllText(
            contents: text.Replace(
                newValue: "0000000000000000",
                oldValue: recorded
            ),
            path: path
        );
        Assert.Throws<InvalidDataException>(testCode: () => LocalEndpointCapability.ReadDescriptor(path: path));
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
