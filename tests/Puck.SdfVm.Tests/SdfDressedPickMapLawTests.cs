using Xunit;
using static Puck.SdfVm.Tests.SdfWorldTablesUploadLawTests;

namespace Puck.SdfVm.Tests;

public sealed class SdfDressedPickMapLawTests {
    private sealed class HostMap : ISdfPickMap {
        public object? Resolve(uint identity) => SdfVisibility.KindOf(identity) switch {
            SdfVisibilityKind.Sdf => $"instance{SdfVisibility.SourceOf(identity) - 1}",
            SdfVisibilityKind.Mesh => $"host{SdfVisibility.SourceOf(identity)}",
            _ => null,
        };
        public string? MaterialName(uint identity, int material) => Resolve(identity) is { } target ? $"{target}/{material}" : null;
    }
    [Fact]
    public void AHostRunCanFollowGroupsAndCopiesResolveThroughTheirSourceRows() {
        var (rows, writer) = SdfMeshRows.Create();
        var group = writer.Allocate(2);
        writer.Set(group, 0, HostDraw("piece", 1), new(Instance: 3));
        writer.Set(group, 1, HostDraw("world", 2), new());
        var host = writer.Allocate(1);
        writer.Set(host, 0, HostDraw("statue", 3), new(Host: 0));
        var copies = writer.Allocate(2);
        writer.Set(copies, 0, rows[host.Start], new(CopiedRow: host.Start));
        writer.Set(copies, 1, rows[group.Start], new(CopiedRow: group.Start));
        writer.Publish();
        var map = new SdfDressedPickMap(new HostMap(), rows);
        uint Mesh(int row) => SdfVisibility.IdentityOf(SdfVisibilityKind.Mesh, (uint)row);
        Assert.Equal("instance3", map.Resolve(Mesh(group.Start)));
        Assert.Null(map.Resolve(Mesh(group.Start + 1)));
        Assert.Equal("host0", map.Resolve(Mesh(host.Start)));
        Assert.Equal("host0", map.Resolve(Mesh(copies.Start)));
        Assert.Equal("instance3/4", map.MaterialName(Mesh(copies.Start + 1), 4));
        var revision = rows.Revision;
        writer.Free(host);
        var reused = writer.Allocate(1);
        writer.Set(reused, 0, HostDraw("statue", 3), new(Host: 0));
        writer.Publish();
        Assert.Equal(host.Start, reused.Start);
        Assert.False(rows.IdentityStands(host.Start, revision));
        Assert.True(rows.IdentityStands(group.Start, revision));
        Assert.Throws<InvalidOperationException>(() => writer.Free(host));
        writer.StartOver(); writer.Publish();
        Assert.Null(map.Resolve(Mesh(host.Start)));
    }
}
