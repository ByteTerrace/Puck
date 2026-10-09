using System.Reflection;
using System.Xml.Linq;

using Xunit;

namespace Puck.Commands.Tests;

public sealed class PackagingTests {
    private static readonly Assembly Packed = typeof(CommandRegistry).Assembly;

    private static XDocument DocumentationFile() {
        var path = Path.ChangeExtension(
            path: Packed.Location,
            extension: ".xml"
        );

        // GenerateDocumentationFile is a tree-wide property, so this asserting rather than skipping
        // is the point: the package ships lib/net10.0/Puck.Commands.xml, and a project that stopped
        // emitting it would publish a surface with no member documentation at all.
        Assert.True(
            condition: File.Exists(path: path),
            userMessage: ("no XML documentation file beside " + Packed.Location)
        );

        return XDocument.Load(uri: path);
    }
    private static string DocumentationIdOf(Type type) =>
        // A doc comment ID spells a nested type with '.' where reflection spells it with '+'; the
        // arity tick on a generic type definition is spelled the same way in both.
        ("T:" + type.FullName!.Replace(
            newChar: '.',
            oldChar: '+'
        ));

    [Fact]
    public void AssemblyCarriesTheSharedPackageIdentity() {
        // build/Packaging.targets stamps Authors/Company/Copyright/Product only when a project sets
        // IsPackable, so an assembly missing them is a project that fell off the packable path —
        // the pack would still succeed and quietly publish an unattributed package.
        Assert.Equal(
            expected: "ByteTerrace",
            actual: Packed.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company
        );
        Assert.Equal(
            expected: "ByteTerrace.Puck",
            actual: Packed.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        );
        Assert.Contains(
            expectedSubstring: "ByteTerrace",
            actualString: (Packed.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty)
        );
        Assert.NotEmpty(collection: (Packed.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty));
    }
    [Fact]
    public void EveryPublicTypeIsDocumentedInTheShippedXml() {
        var documented = DocumentationFile()
            .Descendants(name: "member")
            .Select(selector: static member => member.Attribute(name: "name")?.Value)
            .Where(predicate: static name => (name is not null))
            .ToHashSet(comparer: StringComparer.Ordinal);
        var undocumented = Packed.GetExportedTypes()
            .Select(selector: DocumentationIdOf)
            .Where(predicate: id => !documented.Contains(item: id))
            .OrderBy(
            keySelector: static id => id,
            comparer: StringComparer.Ordinal
        );

        Assert.Equal(
            expected: string.Empty,
            actual: string.Join(
                separator: ", ",
                values: undocumented
            )
        );
    }
}
