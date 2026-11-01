using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldDocumentWalkerLawTests {
    private sealed class Holder {
        public DocumentIdentifier? Name { get; init; }
        public Holder? Shared { get; set; }
    }

    // Records each identifier the walk reaches under the holder it is inside.
    private sealed class OwnerVisitor : IWorldDocumentVisitor {
        private readonly Stack<Holder> m_owners = [];

        public List<(Holder Owner, string Path)> Reached { get; } = [];

        public bool Enter(object value, WorldDocumentSite site) {
            if (value is Holder holder) {
                m_owners.Push(item: holder);

                return true;
            }
            if (value is DocumentIdentifier) {
                Reached.Add(item: (m_owners.Peek(), site.Path));
            }

            return true;
        }
        public void Leave(object value) {
            if (value is Holder) {
                _ = m_owners.Pop();
            }
        }
    }

    private static readonly WorldDocumentWalker Walker = new(surfaces: [typeof(DocumentIdentifier)]);

    [Fact]
    public void AValueSharedByTwoOwnersIsWalkedUnderEach() {
        var shared = new Holder { Name = new DocumentIdentifier(value: "shared") };
        var visitor = new OwnerVisitor();

        Walker.Walk(
            root: new List<Holder> { new() { Shared = shared }, new() { Shared = shared } },
            visitor: visitor
        );

        Assert.Equal(
            expected: [(shared, "[0].shared.name"), (shared, "[1].shared.name")],
            actual: visitor.Reached
        );
    }
    [Fact]
    public void ACycleIsWalkedOnce() {
        var holder = new Holder { Name = new DocumentIdentifier(value: "a") };
        var visitor = new OwnerVisitor();

        holder.Shared = holder;
        Walker.Walk(
            root: holder,
            visitor: visitor
        );

        _ = Assert.Single(collection: visitor.Reached);
    }
    [Fact]
    public void ACollectionRootReachesItsElements() {
        var visitor = new OwnerVisitor();

        Walker.Walk(
            root: new List<Holder> { new() { Name = new DocumentIdentifier(value: "a") } },
            visitor: visitor
        );

        _ = Assert.Single(collection: visitor.Reached);
    }
}
