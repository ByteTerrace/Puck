using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Format;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Pins that no format pass moves a static initializer past a static member it reads. Static field and property
/// initializers run in textual order, so an initializer placed before the member it reads observes that member's
/// default value: a table built from fields declared after it holds nulls, and nothing fails until it is read.
/// </summary>
public sealed class FormatStaticInitializationOrderTests {
    // Each fixture is written in a working order that the reorganizing passes would, but for the dependency, change:
    // the reader sorts before what it reads by name, by accessibility, or by member kind.
    private static readonly string[] Sources = [
        // A public property that reads a private property: grouping by accessibility puts the reader first.
        """
        internal sealed class Theme {
            private static string Zero { get; } = new(c: '0', count: 1);

            public static string[] Absent { get; } = [Zero, Zero];
        }

        """,
        // A table built from fields whose names sort after it.
        """
        internal static class Palette {
            public static readonly object Red = new();
            public static readonly object Green = new();
            public static readonly object[] All = [Red, Green];
        }

        """,
        // A field that reads a property: gathering fields before properties puts the reader first.
        """
        internal static class Mixed {
            public static object Seed { get; } = new();

            public static readonly object[] Table = [Seed];
        }

        """,
        // A static readonly table that reads a mutable static: storage-kind order puts readonly fields first.
        """
        internal static class Counts {
            private static int s_size = 4;
            private static readonly int[] Buffer = new int[s_size];
        }

        """,
        // A property block whose later member's initializer reads an earlier one.
        """
        internal static class Sizes {
            public static int Width { get; } = 8;
            public static int Area { get; } = (Width * Width);
        }

        """,
    ];

    public static TheoryData<string> Fixtures() => new(values: Sources);
    // The pipelines a run can apply: the bare-`format` syntactic set, and the opt-in organizer followed by it.
    public static TheoryData<string, bool> FixturesAndPipelines() {
        var data = new TheoryData<string, bool>();

        foreach (var source in Sources) {
            data.Add(
                p1: source,
                p2: false
            );
            data.Add(
                p1: source,
                p2: true
            );
        }

        return data;
    }

    private static string ApplyAll(string text, IEnumerable<FormatPass> passes) {
        foreach (var pass in passes) {
            text = pass.Apply(node: CSharpSyntaxTree.ParseText(text: text).GetRoot()).ToFullString();
        }

        return text;
    }
    // Every (reader, read) pair among one type's static initializers where the reader's initializer names the read
    // member, as declaration indices in text order.
    private static List<(string Reader, string Read)> Dependencies(string source) {
        var pairs = new List<(string Reader, string Read)>();

        foreach (var type in CSharpSyntaxTree.ParseText(text: source).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()) {
            var initialized = StaticInitializers(type: type);
            var names = initialized.Select(selector: static entry => entry.Name).ToHashSet(comparer: StringComparer.Ordinal);

            foreach (var (name, value) in initialized) {
                foreach (var read in value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()) {
                    if (
                        (read.Parent is not NameColonSyntax) &&
                        names.Contains(item: read.Identifier.ValueText) &&
                        (read.Identifier.ValueText != name)
                    ) {
                        pairs.Add(item: (name, read.Identifier.ValueText));
                    }
                }
            }
        }

        return pairs;
    }
    private static List<string> Order(string source) =>
        CSharpSyntaxTree.ParseText(text: source).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .SelectMany(selector: static type => StaticInitializers(type: type).Select(selector: static entry => entry.Name))
            .ToList();
    private static List<(string Name, ExpressionSyntax Value)> StaticInitializers(TypeDeclarationSyntax type) {
        var entries = new List<(string Name, ExpressionSyntax Value)>();

        foreach (var member in type.Members) {
            if (!member.Modifiers.Any(kind: SyntaxKind.StaticKeyword)) {
                continue;
            }

            switch (member) {
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables) {
                        if (variable.Initializer is { } initializer) {
                            entries.Add(item: (variable.Identifier.ValueText, initializer.Value));
                        }
                    }

                    break;
                case PropertyDeclarationSyntax { Initializer.Value: { } value } property:
                    entries.Add(item: (property.Identifier.ValueText, value));
                    break;
            }
        }

        return entries;
    }

    /// <summary>
    /// Each fixture has a dependency, so the assertion below cannot pass vacuously, and each is already in an order
    /// that initializes what it reads first.
    /// </summary>
    [MemberData(memberName: nameof(Fixtures))]
    [Theory]
    public void EachFixtureReadsOnlyWhatItsTextHasAlreadyInitialized(string source) {
        var order = Order(source: source);
        var dependencies = Dependencies(source: source);

        Assert.NotEmpty(collection: dependencies);
        Assert.All(
            action: pair => Assert.True(condition: (order.IndexOf(item: pair.Read) < order.IndexOf(item: pair.Reader))),
            collection: dependencies
        );
    }
    /// <summary>
    /// After formatting, every static initializer still follows each static member it reads.
    /// </summary>
    [MemberData(memberName: nameof(FixturesAndPipelines))]
    [Theory]
    public void NoPassMovesAStaticInitializerAheadOfWhatItReads(string source, bool organize) {
        var passes = FormatPasses.All.Where(predicate: pass => (pass.Syntactic && (pass.Default || (organize && (pass.Name == "member-groups"))))).ToList();
        var formatted = ApplyAll(
            passes: (organize
                ? passes.OrderBy(keySelector: static pass => (pass.Name != "member-groups"))
                : passes),
            text: source
        );
        var order = Order(source: formatted);

        Assert.All(
            action: pair => Assert.True(
                condition: (order.IndexOf(item: pair.Read) < order.IndexOf(item: pair.Reader)),
                userMessage: $"{pair.Reader} now initializes before {pair.Read}:\n{formatted}"
            ),
            collection: Dependencies(source: source)
        );
    }
}
