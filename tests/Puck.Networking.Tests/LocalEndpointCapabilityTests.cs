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
    // Law: the descriptor publishes the host's identity, a reader recovers it, and a host whose process has exited, or
    // whose process id another process now holds, is recognizably gone without connecting.
    [Fact]
    public void DescriptorPublishesTheHostAndTellsWhenItIsGone() {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) { Assert.Skip(reason: "Local control supports Windows and Linux x64."); return; }
        using var directory = new TemporaryDirectory(prefix: "puck-info-test-");

        var path = directory.PathOf(name: "capability.json");
        var capability = new LocalEndpointCapability(port: 12345, world: new string(c: 'w', count: 600));

        using (capability.WriteDescriptor(path: path)) { }
        var info = LocalEndpointCapability.ReadDescriptor(path: path).Info;

        Assert.Equal(expected: capability.Info, actual: info);
        Assert.Equal(expected: Environment.ProcessId, actual: info.ProcessId);
        Assert.Equal(expected: 512, actual: info.World!.Length);
        Assert.True(condition: info.IsHostRunning());
        Assert.False(condition: (info with { StartedUtc = info.StartedUtc.AddHours(hours: -1) }).IsHostRunning());

        using var exited = System.Diagnostics.Process.Start(startInfo: new System.Diagnostics.ProcessStartInfo(fileName: "dotnet", arguments: "--version") { CreateNoWindow = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        var gone = (info with { ProcessId = exited.Id, StartedUtc = new DateTimeOffset(dateTime: exited.StartTime.ToUniversalTime()) });

        exited.WaitForExit();
        Assert.False(condition: gone.IsHostRunning());
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
