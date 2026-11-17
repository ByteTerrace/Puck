using Xunit;
using static Puck.SdfVm.Tests.SdfWorldTablesUploadLawTests;

namespace Puck.SdfVm.Tests;

public sealed class SdfDressedPickMapLawTests {
    private sealed class HostMap : ISdfPickMap {
        public object? Resolve(uint identity) => SdfVisibility.KindOf(identity: identity) switch {
            SdfVisibilityKind.Sdf => $"instance{(SdfVisibility.SourceOf(identity: identity) - 1)}",
            SdfVisibilityKind.Mesh => $"host{SdfVisibility.SourceOf(identity: identity)}",
            _ => null,
        };
        public string? MaterialName(uint identity, int material) => ((Resolve(identity: identity) is { } target) ? $"{target}/{material}" : null);
    }

    [Fact]
    public void AHostRunCanFollowGroupsAndCopiesResolveThroughTheirSourceRows() {
        var (rows, writer) = SdfMeshRows.Create();
        var group = writer.Allocate(count: 2);

        writer.Set(group, 0, HostDraw(identity: "piece", x: 1), new(Instance: 3));
        writer.Set(group, 1, HostDraw(identity: "world", x: 2), new());
        var host = writer.Allocate(count: 1);

        writer.Set(host, 0, HostDraw(identity: "statue", x: 3), new(Host: 0));
        var copies = writer.Allocate(count: 2);

        writer.Set(copies, 0, rows[host.Start], new(CopiedRow: host.Start));
        writer.Set(copies, 1, rows[group.Start], new(CopiedRow: group.Start));
        writer.Publish();
        var map = new SdfDressedPickMap(inner: new HostMap(), rows: rows);

        uint Mesh(int row) => SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Mesh, source: ((uint)row));
        Assert.Equal("instance3", map.Resolve(identity: Mesh(row: group.Start)));
        Assert.Null(@object: map.Resolve(identity: Mesh(row: (group.Start + 1))));
        Assert.Equal("host0", map.Resolve(identity: Mesh(row: host.Start)));
        Assert.Equal("host0", map.Resolve(identity: Mesh(row: copies.Start)));
        Assert.Equal("instance3/4", map.MaterialName(identity: Mesh(row: (copies.Start + 1)), material: 4));
        var revision = rows.Revision;
        var captured = new SdfPickResult(1, 0, 0, 1, 1, Mesh(row: host.Start), 1, 0,
            new Puck.SignedDistance.SdfProgramBuilder().Build(), revision, map) { RowStamp = new(Revision: revision, Rows: rows) };

        Assert.Equal("host0", captured.Target);

        writer.Free(run: host);
        var reused = writer.Allocate(count: 1);

        writer.Set(reused, 0, HostDraw(identity: "statue", x: 3), new(Host: 0));
        writer.Publish();
        Assert.Equal(host.Start, reused.Start);
        Assert.False(condition: rows.IdentityStands(host.Start, revision));
        Assert.Null(@object: captured.Target);
        Assert.True(condition: rows.IdentityStands(group.Start, revision));
        Assert.Throws<InvalidOperationException>(testCode: () => writer.Free(run: host));
        writer.StartOver(); writer.Publish();
        Assert.Null(@object: map.Resolve(identity: Mesh(row: host.Start)));
    }
}
