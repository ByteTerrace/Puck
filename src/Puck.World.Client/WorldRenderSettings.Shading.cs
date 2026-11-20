using System.Text.Json;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World;

public sealed partial class WorldRenderSettings {
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldDefinition, IReadOnlyList<SdfShadeProgram>> DeclaredShadeSets = new();
    private SdfShadeProgram? m_shadeOverride;
    private SdfProgram? m_shadeScene;
    private readonly Dictionary<string, SdfShadeProgram> m_namedShades = new(StringComparer.Ordinal);

    /// <summary>The live default override; null follows each world's document.</summary>
    public SdfShadeDefinition? ShadeOverride => m_shadeOverride?.Definition;

    /// <summary>Compiles a reloadable default through <see cref="SdfShadeProgram.Compile"/>, which compiles each distinct
    /// source once.</summary>
    /// <param name="definition">The source, or <see langword="null"/> for none.</param>
    /// <returns>The program, or <see langword="null"/> when <paramref name="definition"/> is.</returns>
    /// <exception cref="SdfShadeException"><paramref name="definition"/> is refused.</exception>
    public static SdfShadeProgram? CompileShade(SdfShadeDefinition? definition) => definition is null ? null : SdfShadeProgram.Compile(definition);
    /// <summary>Compiles the program a document's use names, which its world's shades table resolved.</summary>
    /// <param name="shade">The use, or <see langword="null"/> for none.</param>
    /// <returns>The admitted program, or <see langword="null"/> for none.</returns>
    /// <exception cref="SdfShadeException">The program is refused.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="shade"/> names a program no table resolved.</exception>
    public static SdfShadeProgram? CompileShade(Puck.World.Authoring.ShadeReference? shade) => CompileShade(definition: shade?.Definition);

    /// <summary>Returns every palette slot a world's documents declare that can shade: each creation's palette and each
    /// field region's material, with the program it runs (its own, or the document's <c>render.shade</c> default when it
    /// declares none) and its base wrap, and a slot whose look a state cell chooses once more for each case's program
    /// (<see cref="Puck.World.Authoring.PaletteToonSelectDocument"/>). A slot that runs no program is left out.</summary>
    /// <param name="definition">The world definition.</param>
    /// <param name="fallback">The default a slot with no program of its own runs, or <see langword="null"/> for the
    /// document's own.</param>
    /// <returns>The slots, in document order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IEnumerable<(SdfShadeProgram Program, float Wrap)> DeclaredShadeMaterials(WorldDefinition definition, SdfShadeProgram? fallback = null) {
        ArgumentNullException.ThrowIfNull(definition);

        fallback ??= CompileShade(definition.Render.Shade);

        return definition.Creations.SelectMany(selector: static prototype => (prototype.Document.Palette ?? []))
            .Concat(second: definition.FieldRegions.SelectMany(selector: static region => region.Fields)
                .Where(predicate: static field => (field.Material is not null)).Select(selector: static field => field.Material!))
            .SelectMany(selector: static material => ((material.ToonSelect?.Cases is { } cases)
                ? cases.Select(selector: look => (Shade: look.Shade, material.Wrap)).Prepend(element: (material.Shade, material.Wrap))
                : [(material.Shade, material.Wrap)]))
            .Select(selector: material => (Program: (CompileShade(material.Shade) ?? fallback), Wrap: (material.Wrap ?? 0f)))
            .Where(predicate: static material => (material.Program is not null))
            .Select(selector: static material => (material.Program!, material.Wrap));
    }
    /// <summary>Returns the distinct programs a world's documents declare, the default included, compiled once per
    /// definition: the set the renderer specializes (<see cref="SdfFrame.ShadeDeclared"/>) and <c>world prepare</c>
    /// prewarms, one list for one definition.</summary>
    /// <param name="definition">The world definition.</param>
    /// <returns>The programs, one per identity, ordered by identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SdfShadeProgram> DeclaredShades(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        return DeclaredShadeSets.GetValue(key: definition, derive: static definition => SdfShadeHlsl.Canonical(
            programs: DeclaredShadeMaterials(definition: definition).Select(selector: static material => material.Program)
                .Concat(second: ((CompileShade(definition.Render.Shade) is { } fallback) ? [fallback] : []))));
    }

    /// <summary>Resolves the live override or a world's own default and inventories its named material programs.</summary>
    public SdfShadeProgram? ResolveShade(WorldRenderDefaults render, SdfProgram scene) {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(scene);
        if (!ReferenceEquals(m_shadeScene, scene)) {
            m_namedShades.Clear();
            foreach (var program in scene.Appearance.ShadingPrograms) { m_namedShades[program.Definition.Name] = program; }
            m_shadeScene = scene;
        }
        var authored = CompileShade(render.Shade);
        if (authored is not null) { m_namedShades[authored.Definition.Name] = authored; }
        return m_shadeOverride ?? authored;
    }

    /// <summary>Finds a named admitted program in the current scene.</summary>
    public SdfShadeProgram? NamedShade(string name) => m_namedShades.GetValueOrDefault(name);

    /// <summary>Applies an admitted lever payload; null restores the document default.</summary>
    public void SetShadeDefault(string? payload) {
        var definition = payload is null ? null : JsonSerializer.Deserialize(payload, WorldJsonContext.Default.SdfShadeDefinition);
        m_shadeOverride = CompileShade(definition);
        m_revision++;
    }
}
