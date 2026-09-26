using System.Text.Json.Serialization;
using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

/// <summary>
/// A <c>puck.shader.package.v1</c> manifest: the source closure of one graph document or one-off shader, and what
/// compiling it requires. A package is a directory holding this manifest as <see cref="FileName"/> beside every file it
/// lists, each at its logical path. The package carries sources rather than bytecode, so loading one compiles it with
/// the compiler it pins.
/// <para>The compile facts come from the compiler rather than being assembled beside it: <see cref="Compiler"/>'s
/// revisions and each variant's <see cref="ShaderPackageVariant.Stages"/> are the <see cref="ShaderCompileIdentity"/>
/// the compiler hashed into the pass's cache key, and every file's pin is the content hash that key hashed.</para>
/// </summary>
/// <param name="Schema">The document's schema, <see cref="SchemaName"/>.</param>
/// <param name="Name">The pipeline's name.</param>
/// <param name="Document">The logical path of the graph document or one-off shader source that loading starts
/// from.</param>
/// <param name="Compiler">The compiler the package requires.</param>
/// <param name="Capabilities">The backend capabilities the pipeline requires.</param>
/// <param name="Files">Every authored file of the closure, in ordinal order of logical path.</param>
/// <param name="Passes">How each pass compiled and what it compiled to, in the plan's execution order.</param>
public sealed record ShaderPackageManifest(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    string Document,
    ShaderPackageCompiler Compiler,
    ShaderPackageCapabilities Capabilities,
    IReadOnlyList<ShaderPackageFile> Files,
    IReadOnlyList<ShaderPackagePass> Passes
) {
    /// <summary>The manifest's schema name.</summary>
    public const string SchemaName = "puck.shader.package.v1";
    /// <summary>The manifest's file name inside a package directory.</summary>
    public const string FileName = "puck.shader.package.json";
}
/// <summary>The compiler a package requires: the compiler revision its passes were compiled under, and the version every
/// native tool reported.</summary>
/// <param name="Version">The compiler's revision (<see cref="ShaderCompileIdentity.Compiler"/>).</param>
/// <param name="Tools">Every native tool a pass's steps run, in ordinal order of name.</param>
public sealed record ShaderPackageCompiler(string Version, IReadOnlyList<ShaderPackageTool> Tools);
/// <summary>One native tool a package pins.</summary>
/// <param name="Name">The tool's name, as a <see cref="ShaderCompileStep.Tool"/> spells it.</param>
/// <param name="Version">The first line the tool's version query prints
/// (<see cref="ShaderCompiler.ToolVersionAsync"/>).</param>
public sealed record ShaderPackageTool(string Name, string Version);
/// <summary>One file of a package.</summary>
/// <param name="Path">The logical path: relative to the package root, with forward slashes and no <c>.</c> or
/// <c>..</c> segment.</param>
/// <param name="Pin">The <c>sha256/&lt;hex64&gt;</c> content pin of the file's UTF-8 text
/// (<see cref="ShaderSourceClosure.HashOf"/>).</param>
/// <param name="Bytes">The file's length on disk, in bytes.</param>
public sealed record ShaderPackageFile(string Path, string Pin, long Bytes);
/// <summary>How one pass compiled and what it compiled to.</summary>
/// <param name="Name">The pass's name.</param>
/// <param name="Interface">The pass's interface as its canonical JSON (<see cref="ShaderInterface.ToJson"/>), whose pin
/// is the interface's hash (<see cref="ShaderInterface.Hash"/>), which versions the declarations and every
/// binary.</param>
/// <param name="Declarations">The declarations generated from the interface, at the path the pass's source includes
/// them from.</param>
/// <param name="Variants">The pass's precompiled binaries, one entry per variant: <see cref="ShaderPackageVariant.DefaultName"/>,
/// then one for each tier its graph declares (<see cref="RenderGraphDefinition.Variants"/>), cheapest first. Every
/// variant reads the same interface, so a tier cannot change what a pass reads.</param>
public sealed record ShaderPackagePass(
    string Name,
    ShaderPackageFile Interface,
    ShaderPackageFile Declarations,
    IReadOnlyList<ShaderPackageVariant> Variants
);
/// <summary>One variant of a pass: how it compiled and its precompiled binaries. Every pass carries
/// <see cref="DefaultName"/>, compiled with no tier defined, which a <c>views.graphs</c> row naming no tier, or a tier
/// its graph does not declare, loads; each other variant is a tier the graph declares, named for it
/// (<see cref="QualityTiers.Name"/>) and compiled with it defined (<see cref="ShaderCompiler.StepsOf"/>).</summary>
/// <param name="Name">The variant's name.</param>
/// <param name="Stages">Each stage's entry point, profile, and native tool steps, as the compiler identified
/// them (<see cref="ShaderCompileIdentity.Stages"/>).</param>
/// <param name="Binaries">One SPIR-V module and one DXIL container per stage, in stage order, SPIR-V first.</param>
public sealed record ShaderPackageVariant(string Name, IReadOnlyList<ShaderCompileStage> Stages, IReadOnlyList<ShaderPackageBinary> Binaries) {
    /// <summary>The name of the variant compiled for no tier.</summary>
    public const string DefaultName = "default";

    /// <summary>Returns the name of the variant a tier loads.</summary>
    /// <param name="tier">The tier, or <see langword="null"/> for none.</param>
    /// <returns><see cref="DefaultName"/> for no tier, and the tier's name otherwise.</returns>
    public static string NameOf(QualityTier? tier) => ((tier is { } named)
        ? QualityTiers.Name(tier: named)
        : DefaultName);
    /// <summary>Spells the tier a row asked for and the variant it got: the variant's name when they agree, and
    /// <c>&lt;asked&gt;-&gt;&lt;variant&gt;</c>, such as <c>high-&gt;default</c>, when the graph declares no variant of the tier
    /// asked for.</summary>
    /// <param name="requested">The tier the row names, or <see langword="null"/> for none.</param>
    /// <param name="variant">The variant's tier (<see cref="RenderGraphDefinition.VariantOf"/>), or
    /// <see langword="null"/> for <see cref="DefaultName"/>.</param>
    /// <returns>The spelling.</returns>
    public static string Spell(QualityTier? requested, QualityTier? variant) => ((requested == variant)
        ? NameOf(tier: variant)
        : $"{NameOf(tier: requested)}->{NameOf(tier: variant)}");
    /// <summary>Describes a load's variant as a clause of its outcome message: nothing for a row naming no tier,
    /// <c> at tier high</c> for a declared tier, and <c> at tier high-&gt;default</c> with the reason when the graph declares
    /// no variant of it.</summary>
    /// <param name="requested">The tier the row names, or <see langword="null"/> for none.</param>
    /// <param name="variant">The variant's tier, or <see langword="null"/> for <see cref="DefaultName"/>.</param>
    /// <returns>The clause, empty or starting with a space.</returns>
    public static string Describe(QualityTier? requested, QualityTier? variant) => ((requested is null)
        ? string.Empty
        : $" at tier {Spell(requested: requested, variant: variant)}{((requested == variant) ? string.Empty : $" (the graph declares no {NameOf(tier: requested)} variant)")}");
}
/// <summary>One precompiled binary of a package.</summary>
/// <param name="Stage">The stage it runs.</param>
/// <param name="Target">What it is: <see cref="SpirvTarget"/> for Vulkan or <see cref="DxilTarget"/> for Direct3D
/// 12.</param>
/// <param name="Path">The logical path, as <see cref="ShaderPackageFile.Path"/> spells one.</param>
/// <param name="Pin">The <c>sha256/&lt;hex64&gt;</c> content pin of the binary's bytes.</param>
/// <param name="Bytes">The binary's length, in bytes.</param>
public sealed record ShaderPackageBinary(ShaderStage Stage, string Target, string Path, string Pin, long Bytes) {
    /// <summary>The target of a SPIR-V module.</summary>
    public const string SpirvTarget = "spirv";
    /// <summary>The target of a DXIL container.</summary>
    public const string DxilTarget = "dxil";
}
/// <summary>The backend capabilities a package's pipeline requires, derived from its plan.</summary>
/// <param name="TargetFloor">The Vulkan version and Direct3D shader model every stage targets.</param>
/// <param name="ImageFormats">Every image format a storage of the plan declares, in ordinal order.</param>
/// <param name="Buffers">Whether the plan declares a buffer storage, bound as a raw view.</param>
/// <param name="WorkgroupInvocations">The largest compute workgroup, in invocations, or zero with no compute
/// pass.</param>
/// <param name="ParameterBytes">The largest packed parameter block of any pass, in bytes.</param>
public sealed record ShaderPackageCapabilities(
    ShaderTargetFloor TargetFloor,
    IReadOnlyList<string> ImageFormats,
    bool Buffers,
    uint WorkgroupInvocations,
    uint ParameterBytes
);
/// <summary>Source-generated metadata for <see cref="ShaderPackageManifest"/>: strict on read, canonical on write.</summary>
[JsonSerializable(typeof(ShaderPackageManifest))]
[JsonSourceGenerationOptions(
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true
)]
public partial class ShaderPackageJsonContext : JsonSerializerContext {
}
