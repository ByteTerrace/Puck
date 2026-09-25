using System.Text.RegularExpressions;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// The ports of a document pass as members of its interface's pass group, in document order: its inputs, then a compute
/// pass's outputs. A graphics pass's outputs are attachments, not bindings, so they declare no member.
/// <list type="bullet">
/// <item><description>An image input is a sampled image named by the port's identifier and a sampler named for it with
/// <c>Sampler</c> appended.</description></item>
/// <item><description>A buffer input is a raw buffer the pass reads, and a compute buffer output one it reads and
/// writes.</description></item>
/// <item><description>A compute image output is a storage image of the resource's format.</description></item>
/// </list>
/// A port's identifier (<see cref="Identifier"/>) is its <see cref="ResourceReference.As"/> when it names one, and
/// otherwise its resource's name in camel case, with <c>previous</c> leading a previous-frame input's.
/// </summary>
public static partial class ShaderPipelinePassPorts {
    /// <summary>What a port's sampler identifier appends to its image's.</summary>
    public const string SamplerSuffix = "Sampler";

    [GeneratedRegex(pattern: "^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex IdentifierPattern();

    /// <summary>Returns a port's identifier: its <see cref="ResourceReference.As"/>, or its resource's name in camel case
    /// (each hyphen, underscore or period removed and the letter after it capitalized), with <c>previous</c> leading a
    /// previous-frame input's and the name's first letter then capitalized.</summary>
    /// <param name="reference">The port.</param>
    /// <returns>The identifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    public static string Identifier(ResourceReference reference) {
        ArgumentNullException.ThrowIfNull(argument: reference);

        if (reference.As is { } named) {
            return named;
        }

        var words = reference.Name.Split(separator: ['-', '_', '.'], options: StringSplitOptions.RemoveEmptyEntries);
        var camel = string.Concat(values: words.Select(selector: static (word, index) => ((index == 0)
            ? (char.ToLowerInvariant(c: word[0]) + word[1..])
            : (char.ToUpperInvariant(c: word[0]) + word[1..]))));

        return (reference.PreviousFrame
            ? ("previous" + char.ToUpperInvariant(c: camel[0]) + camel[1..])
            : camel);
    }
    /// <summary>Returns a document pass's port members, in document order.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="resources">The graph's resource versions by name.</param>
    /// <returns>The members, each in <see cref="ShaderInterfaceGroup.Pass"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pass"/> or <paramref name="resources"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">A port names an <c>as</c> that is not an HLSL identifier, names a version
    /// the graph does not declare, or reads as a frame value, a config field of the pass or another member of it.</exception>
    public static IReadOnlyList<ShaderInterfaceMember> Members(ShaderPipelinePass pass, IReadOnlyDictionary<string, ShaderPipelineResource> resources) {
        ArgumentNullException.ThrowIfNull(argument: pass);
        ArgumentNullException.ThrowIfNull(argument: resources);

        var members = new List<ShaderInterfaceMember>();
        var owners = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        void Add(ShaderInterfaceMember member, string port) {
            if (
                ShaderFrameInterface.PushedMembers.Any(predicate: value => string.Equals(
                    a: value.Name,
                    b: member.Name,
                    comparisonType: StringComparison.Ordinal
                )) ||
                (pass.Config?.ContainsKey(key: member.Name) == true)
            ) {
                throw new InvalidDataException(message: $"pass '{pass.Name}' {port} reads as '{member.Name}', which a frame value or config field of the pass already names; name the port with \"as\".");
            }
            if (owners.TryGetValue(
                key: member.Name,
                value: out var other
            )) {
                throw new InvalidDataException(message: $"pass '{pass.Name}' ports {other} and {port} both read as '{member.Name}'; name one with \"as\".");
            }

            owners[member.Name] = port;
            members.Add(item: member);
        }

        foreach (var input in pass.InputReferences) {
            var identifier = Checked(
                pass: pass,
                reference: input
            );
            var port = $"input '{input.Name}'";

            if (Resource(
                name: input.Name,
                pass: pass,
                resources: resources
            ).Kind == ShaderPipelineResourceKind.Buffer) {
                Add(
                    member: ShaderInterfaceMember.ReadOnlyBuffer(
                        group: ShaderInterfaceGroup.Pass,
                        name: identifier
                    ),
                    port: port
                );
                continue;
            }

            Add(
                member: ShaderInterfaceMember.SampledImage(
                    group: ShaderInterfaceGroup.Pass,
                    name: identifier,
                    type: ShaderValueType.Float4
                ),
                port: port
            );
            Add(
                member: ShaderInterfaceMember.Sampler(
                    group: ShaderInterfaceGroup.Pass,
                    name: (identifier + SamplerSuffix)
                ),
                port: $"{port}'s sampler"
            );
        }

        if (pass.IsGraphics) {
            return members;
        }

        foreach (var output in pass.OutputReferences) {
            var identifier = Checked(
                pass: pass,
                reference: output
            );
            var resource = Resource(
                name: output.Name,
                pass: pass,
                resources: resources
            );
            var port = $"output '{output.Name}'";

            Add(
                member: ((resource.Kind == ShaderPipelineResourceKind.Buffer)
                    ? ShaderInterfaceMember.ReadWriteBuffer(
                        group: ShaderInterfaceGroup.Pass,
                        name: identifier
                    )
                    : ShaderInterfaceMember.StorageImage(
                        format: (Enum.TryParse<GpuPixelFormat>(
                            ignoreCase: true,
                            result: out var format,
                            value: resource.Format
                        )
                            ? format
                            : throw new InvalidDataException(message: $"pass '{pass.Name}' {port} writes a resource whose format '{resource.Format}' is not an image format.")),
                        group: ShaderInterfaceGroup.Pass,
                        name: identifier,
                        type: ShaderValueType.Float4
                    )),
                port: port
            );
        }

        return members;
    }

    /// <summary>Returns why a document pass's source never names one of its ports, or <see langword="null"/> when it names
    /// every one: the refusal a load reports as <c>SHADERPIPE_INTERFACE</c> before it compiles. Renaming a resource
    /// renames the identifier its port generates, so a source still reading the old name would otherwise fail to compile
    /// with no word about the port; the refusal names the pass, the port, the identifier and <c>as</c>, which binds the
    /// port to the identifier the source reads. A port is named when its identifier appears as a whole token outside
    /// comments in the source or a file it includes; a sampler, which a source may leave unread, is not checked.</summary>
    /// <param name="pass">The document pass.</param>
    /// <param name="texts">The text of the pass's source and of every file it includes other than its generated
    /// interface.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pass"/> or <paramref name="texts"/> is
    /// <see langword="null"/>.</exception>
    public static string? UnnamedPort(ShaderPipelinePass pass, IEnumerable<string> texts) {
        ArgumentNullException.ThrowIfNull(argument: pass);
        ArgumentNullException.ThrowIfNull(argument: texts);

        var code = string.Join(
            separator: '\n',
            values: texts.Select(selector: static text => CommentPattern().Replace(
                input: text,
                replacement: " "
            ))
        );
        var ports = pass.InputReferences.Select(selector: static reference => (Reference: reference, Role: "input")).Concat(second: (pass.IsGraphics
            ? []
            : pass.OutputReferences.Select(selector: static reference => (Reference: reference, Role: "output"))));

        foreach (var (reference, role) in ports) {
            var identifier = Identifier(reference: reference);

            if (!Regex.IsMatch(
                input: code,
                pattern: $"(?<![A-Za-z0-9_]){Regex.Escape(str: identifier)}(?![A-Za-z0-9_])"
            )) {
                return $"Pass '{pass.Name}' {role} '{reference.Name}' reads as '{identifier}', which '{Path.GetFileName(path: pass.Source)}' never names; give the port \"as\": \"<identifier>\" with the name the source reads it by.";
            }
        }

        return null;
    }

    [GeneratedRegex(pattern: @"//[^\n]*|/\*.*?\*/", options: RegexOptions.Singleline)]
    private static partial Regex CommentPattern();
    // A port's identifier, refusing an "as" that is not an HLSL identifier.
    private static string Checked(ShaderPipelinePass pass, ResourceReference reference) {
        if (
            (reference.As is { } named) &&
            !IdentifierPattern().IsMatch(input: named)
        ) {
            throw new InvalidDataException(message: $"pass '{pass.Name}' port '{reference.Name}' names \"as\": \"{named}\", which is not an HLSL identifier (an ASCII letter followed by ASCII letters and digits).");
        }

        return Identifier(reference: reference);
    }
    private static ShaderPipelineResource Resource(string name, ShaderPipelinePass pass, IReadOnlyDictionary<string, ShaderPipelineResource> resources) =>
        (resources.TryGetValue(
            key: name,
            value: out var resource
        )
            ? resource
            : throw new InvalidDataException(message: $"pass '{pass.Name}' port '{name}' names no resource version the graph declares."));
}
