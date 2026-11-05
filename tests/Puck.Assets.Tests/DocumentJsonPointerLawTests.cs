using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.Assets.Tests;

// Every reader of a JSON pointer in the tree takes its steps from DocumentJsonPointer, so what one admits every one
// admits. These laws fix what its walk admits and how a missing step is refused.
public sealed class DocumentJsonPointerLawTests {
    private static readonly JsonNode Document = JsonNode.Parse(json: """{ "a/b": { "c~d": [ 10, { "x": null } ] }, "": 7 }""")!;

    [InlineData("", true)]
    [InlineData("/a~1b/c~0d/0", true)]
    [InlineData("/a~1b/c~0d/1/x", true)]
    [InlineData("/", true)]
    [InlineData("/a~1b/c~0d/2", false)]
    [InlineData("/a~1b/c~0d/01", false)]
    [InlineData("/a~1b/c~0d/-1", false)]
    [InlineData("/a~1b/c~0d/+0", false)]
    [InlineData("/missing/deeper", false)]
    [InlineData("a~1b", false)]
    [InlineData("/a~2b", false)]
    [InlineData("/a~", false)]
    [Theory]
    public void APointerNamesANodeOnlyWhenEveryStepIsThere(string pointer, bool exists) {
        Assert.Equal(expected: exists, actual: DocumentJsonPointer.Exists(pointer: pointer, root: Document));
        Assert.Equal(expected: exists, actual: DocumentJsonPointer.TryAt(node: out _, pointer: pointer, root: Document));
    }
    [Fact]
    public void AMemberHoldingNullIsPresentAndReadsAsNull() {
        Assert.True(condition: DocumentJsonPointer.TryAt(node: out var node, pointer: "/a~1b/c~0d/1/x", root: Document));
        Assert.Null(@object: node);
        Assert.Null(@object: DocumentJsonPointer.At(pointer: "/a~1b/c~0d/1/x", root: Document));
        Assert.Null(@object: DocumentJsonPointer.Require(pointer: "/a~1b/c~0d/1/x", root: Document));
    }
    [Fact]
    public void AnEmptyMemberNameIsAnOrdinarySegment() {
        Assert.Equal(expected: 7, actual: DocumentJsonPointer.At(pointer: "/", root: Document)!.GetValue<int>());
        Assert.Equal(expected: [string.Empty], actual: DocumentJsonPointer.Segments(pointer: "/"));
        Assert.Empty(collection: DocumentJsonPointer.Segments(pointer: string.Empty));
    }
    [Fact]
    public void ARefusedPointerNamesItself() {
        var absent = Assert.Throws<ArgumentException>(testCode: () => DocumentJsonPointer.Require(pointer: "/missing", root: Document));
        var malformed = Assert.Throws<ArgumentException>(testCode: () => DocumentJsonPointer.Segments(pointer: "a"));

        Assert.Contains(expectedSubstring: "'/missing'", actualString: absent.Message);
        Assert.Contains(expectedSubstring: "'a'", actualString: malformed.Message);
    }
    [Fact]
    public void ASegmentRoundTripsThroughEscapeAndSegments() {
        const string Name = "a/b~c";
        var pointer = ("/" + DocumentJsonPointer.Escape(value: Name));

        Assert.Equal(expected: [Name], actual: DocumentJsonPointer.Segments(pointer: pointer));
        Assert.Equal(expected: string.Empty, actual: DocumentJsonPointer.Parent(pointer: pointer));
        Assert.Equal(expected: "/a~1b", actual: DocumentJsonPointer.Parent(pointer: "/a~1b/c~0d"));
    }
    [Fact]
    public void NearestStopsAtTheDeepestNodeTheDocumentHolds() {
        Assert.Same(expected: Document["a/b"], actual: DocumentJsonPointer.Nearest(pointer: "/a~1b/missing/deeper", root: Document));
        Assert.Same(expected: Document, actual: DocumentJsonPointer.Nearest(pointer: "/nothing", root: Document));
    }
    [Fact]
    public void WalkingValuesNamesEveryScalarByTheSamePointerTheWalkReads() {
        var seen = new List<string>();

        DocumentJsonPointer.WalkValues(node: Document, pointer: string.Empty, visit: (value, pointer) => {
            seen.Add(item: pointer);
            Assert.Same(expected: value, actual: DocumentJsonPointer.At(pointer: pointer, root: Document));
        });
        Assert.Equal(expected: ["/a~1b/c~0d/0", "/"],actual: seen);
    }
}
