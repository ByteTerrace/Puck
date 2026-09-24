using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Assets;

namespace Puck.Shaders;

/// <summary>One read of the source a pipeline instance names: its content identity, the definition it declares, and
/// the identity of that definition's parameter schemas. A host records the read its installed graph was compiled
/// from, and a commit of that instance's parameter overrides is checked against a fresh read, so a value tuned
/// against one revision of a source is never recorded against another.</summary>
/// <remarks>For a pipeline document or a one-off shader, the source identity covers only the file the instance names;
/// its pass sources and includes are not part of it. For a package, it is the pin of the canonical manifest, which pins
/// every file of the source closure, so an edit anywhere in the closure is a different source.</remarks>
public sealed class ShaderPipelineSource {
    private ShaderPipelineSource(string path, ShaderPipelineDefinition definition, string sourceIdentity) {
        Path = path;
        Definition = definition;
        SourceIdentity = sourceIdentity;
        ConfigIdentity = ConfigIdentityOf(definition: definition);
    }

    /// <summary>Gets the identity of the definition's parameter schemas: every pass's config fields with their types
    /// and ranges, in ordinal pass and field order. Descriptions and defaults are not part of it, because neither
    /// changes which values a pass admits.</summary>
    public string ConfigIdentity { get; }
    /// <summary>Gets the definition the source declares.</summary>
    public ShaderPipelineDefinition Definition { get; }
    /// <summary>Gets the full path that was read: the source file, or the package directory.</summary>
    public string Path { get; }
    /// <summary>Gets the <see cref="ContentPin"/> text of the source file's bytes, or of a package's canonical
    /// manifest.</summary>
    public string SourceIdentity { get; }

    private static void AppendBound(StringBuilder builder, double? bound) {
        if (bound is { } value) {
            builder.Append(value: value.ToString(
                format: "R",
                provider: CultureInfo.InvariantCulture
            ));
        }
    }

    /// <summary>Computes the identity of a definition's parameter schemas, as <see cref="ConfigIdentity"/> does.</summary>
    /// <param name="definition">The pipeline definition.</param>
    /// <returns>The <see cref="ContentPin"/> text of the canonical schema listing.</returns>
    public static string ConfigIdentityOf(ShaderPipelineDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var builder = new StringBuilder();

        foreach (var pass in definition.Passes.OrderBy(keySelector: static pass => pass.Name, comparer: StringComparer.Ordinal)) {
            if (pass.Config is not { } config) {
                continue;
            }

            foreach (var (field, declaration) in config.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
                builder.Append(value: pass.Name).Append(value: '\u001f').Append(value: field).Append(value: '\u001f');
                builder.Append(value: declaration.Type.ToString()).Append(value: '\u001f');
                AppendBound(
                    bound: declaration.Min,
                    builder: builder
                );
                builder.Append(value: '\u001f');
                AppendBound(
                    bound: declaration.Max,
                    builder: builder
                );
                builder.Append(value: '\n');
            }
        }

        return ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: builder.ToString())).ToString();
    }
    /// <summary>Reads a pipeline instance's source the way <see cref="ShaderPackager.LoadSource"/> loads it: a directory
    /// as a <c>puck.shader.package.v1</c> package, a <c>.json</c> path as a pipeline document, and an <c>.hlsl</c> path as a
    /// one-off shader forming a one-pass pipeline.</summary>
    /// <param name="name">The instance name, which names a one-off shader's pipeline. A package's pipeline is named by
    /// its manifest.</param>
    /// <param name="path">The full path of the source.</param>
    /// <param name="source">The read, when this returns <see langword="true"/>.</param>
    /// <param name="reason">Why the source could not be read, when this returns <see langword="false"/>. A package
    /// refusal carries its bracketed code.</param>
    /// <returns><see langword="true"/> when the source was read and declares a definition.</returns>
    public static bool TryRead(string name, string path, [NotNullWhen(returnValue: true)] out ShaderPipelineSource? source, out string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
        source = null;

        try {
            source = (ShaderPackager.IsPackage(path: path)
                ? ReadPackage(path: path)
                : ReadFile(
                    name: name,
                    path: path
                ));
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException or ShaderClosureRefusedException)) {
            reason = $"cannot read pipeline source '{path}': {exception.Message}";

            return false;
        }
    }

    // A document or one-off shader: its identity is the pin of the file's bytes, the text the definition is parsed from.
    private static ShaderPipelineSource ReadFile(string name, string path) {
        var bytes = File.ReadAllBytes(path: path);
        using var reader = new StreamReader(stream: new MemoryStream(buffer: bytes));

        return new ShaderPipelineSource(
            definition: ShaderPipelineLoader.ParseDefinition(
                name: name,
                path: path,
                text: reader.ReadToEnd()
            ),
            path: path,
            sourceIdentity: ContentPin.Compute(content: bytes).ToString()
        );
    }
    // A package: its manifest is read and every listed file verified, and its identity is the pin of the canonical
    // manifest, which pins every file of the closure.
    private static ShaderPipelineSource ReadPackage(string path) {
        var manifest = ShaderPackager.Open(package: path);

        return new ShaderPipelineSource(
            definition: ShaderPipelineLoader.ReadDefinition(
                name: manifest.Name,
                path: System.IO.Path.Combine(
                    path1: path,
                    path2: manifest.Document
                )
            ),
            path: path,
            sourceIdentity: ContentPin.Compute(content: ShaderPackager.Write(manifest: manifest)).ToString()
        );
    }

    /// <summary>Determines whether a name is an image version of this definition, the kind of resource an output
    /// selection can show.</summary>
    /// <param name="name">The candidate output name.</param>
    /// <returns><see langword="true"/> when the definition declares an image resource by that name.</returns>
    public bool DeclaresImage(string name) => Definition.Resources.Any(predicate: resource => (
        (resource.Kind == ShaderPipelineResourceKind.Image) &&
        string.Equals(
            a: resource.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        )
    ));
    /// <summary>Binds per-pass parameter overrides through each pass's config schema, the same binder a live
    /// parameter change and an installed graph use: every key names a pass that declares config, and every value is
    /// that pass's config object, whose absent fields keep the pass's declared defaults.</summary>
    /// <param name="overrides">The overrides by pass name, or <see langword="null"/> for none.</param>
    /// <param name="reason">The first refusal in ordinal pass order, naming the pass and the field.</param>
    /// <returns><see langword="true"/> when every override binds.</returns>
    public bool TryBindOverrides(IReadOnlyDictionary<string, JsonElement>? overrides, out string reason) {
        reason = string.Empty;

        if (overrides is null) {
            return true;
        }

        foreach (var (passName, config) in overrides.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            var pass = Definition.Passes.FirstOrDefault(predicate: candidate => string.Equals(
                a: candidate.Name,
                b: passName,
                comparisonType: StringComparison.Ordinal
            ));

            if (pass is null) {
                reason = $"'{passName}' is not a pass of pipeline '{Definition.Name}'.";

                return false;
            }
            if (pass.Config is not { Count: > 0 }) {
                reason = $"pass '{passName}' declares no config fields.";

                return false;
            }

            ShaderPipelineParameterLayout layout;

            try {
                layout = ShaderPipelineParameterLayout.Resolve(pass: pass);
            } catch (InvalidDataException exception) {
                reason = $"pass '{passName}': {exception.Message}";

                return false;
            }

            if (!layout.TryBind(
                config: config,
                reason: out var bindReason,
                values: out _
            )) {
                reason = $"pass '{passName}': {bindReason}";

                return false;
            }
        }

        return true;
    }
}
