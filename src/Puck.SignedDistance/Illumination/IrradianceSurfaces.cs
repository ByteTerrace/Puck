namespace Puck.SignedDistance.Illumination;

/// <summary>
/// What the light transport reads at a surface and at a world exit: a material's diffuse albedo and outgoing
/// self-emission, the direct light arriving at a surface point, and the sky in a direction. Colours are linear RGB in
/// the working space's display-referred units, as the views pass shades them. The illumination reference and the cache
/// model read one instance, so both answer the same question.
/// </summary>
public sealed class IrradianceSurfaces {
    /// <summary>Initializes a new instance of the <see cref="IrradianceSurfaces"/> class.</summary>
    /// <param name="albedo">The function returning a material's diffuse albedo, which bounces incident light.</param>
    /// <param name="emission">The function returning a material's outgoing self-emission.</param>
    /// <param name="direct">The function returning the direct light's normalized irradiance at a surface point, with its
    /// own visibility already applied, given the point, its unit normal and its material; <see langword="null"/> for
    /// no direct light.</param>
    /// <param name="sky">The function returning the sky's radiance in a unit direction at a world exit;
    /// <see langword="null"/> for a black sky.</param>
    /// <param name="screens">Screen lights' normalized irradiance at a hit, independently attributed from analytic
    /// light; null for none.</param>
    /// <param name="reflection">Optional point-specific diffuse reflectance; null uses the material albedo.</param>
    /// <exception cref="ArgumentNullException"><paramref name="albedo"/> or <paramref name="emission"/> is
    /// <see langword="null"/>.</exception>
    public IrradianceSurfaces(Func<int, Double3> albedo, Func<int, Double3> emission, Func<Double3, Double3, int, Double3>? direct = null, Func<Double3, Double3>? sky = null, Func<Double3, Double3, int, Double3>? screens = null, Func<Double3, Double3, int, Double3>? reflection = null) {
        ArgumentNullException.ThrowIfNull(argument: albedo);
        ArgumentNullException.ThrowIfNull(argument: emission);

        Albedo = albedo;
        Reflection = (reflection ?? ((_, _, material) => albedo(material)));
        Emission = emission;
        Direct = (direct ?? (static (_, _, _) => Double3.Zero));
        Sky = (sky ?? (static _ => Double3.Zero));
        Screens = (screens ?? (static (_, _, _) => Double3.Zero));
    }

    /// <summary>Gets the function returning a material's diffuse albedo.</summary>
    public Func<int, Double3> Albedo { get; }
    /// <summary>Gets the point-specific diffuse reflectance, including reflected-light attenuation. Emission is independent.</summary>
    public Func<Double3, Double3, int, Double3> Reflection { get; }
    /// <summary>Gets the function returning a material's outgoing self-emission.</summary>
    public Func<int, Double3> Emission { get; }
    /// <summary>Gets the function returning the direct light's normalized irradiance at a surface point.</summary>
    public Func<Double3, Double3, int, Double3> Direct { get; }
    /// <summary>Gets the function returning the sky's radiance in a direction.</summary>
    public Func<Double3, Double3> Sky { get; }
    /// <summary>Gets the screen lights' normalized irradiance at a hit.</summary>
    public Func<Double3, Double3, int, Double3> Screens { get; }

    /// <summary>Creates the surfaces of the materials a program was built with: diffuse albedo
    /// <c>albedo × (1 − metal) × bleed</c>, and self-emission
    /// <c>albedo × emissive × bleed</c>. Receive and the view's artistic fill do not become transport sources.</summary>
    /// <param name="materials">The materials, indexed as the program's material identifiers are.</param>
    /// <param name="direct">The function returning the direct light at a surface point; <see langword="null"/> for none.</param>
    /// <param name="sky">The function returning the sky's radiance in a direction; <see langword="null"/> for black.</param>
    /// <param name="screens">Screen lights' normalized irradiance at a hit; null for none.</param>
    /// <param name="reflection">Optional point-specific diffuse reflectance; null uses the material diffuse albedo.</param>
    /// <returns>The surfaces.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public static IrradianceSurfaces FromMaterials(IReadOnlyList<SdfMaterial> materials, Func<Double3, Double3, int, Double3>? direct = null, Func<Double3, Double3>? sky = null, Func<Double3, Double3, int, Double3>? screens = null, Func<Double3, Double3, int, Double3>? reflection = null) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        return new IrradianceSurfaces(
            albedo: material => {
                var entry = materials[material];
                var diffuse = (1.0 - entry.Metal);
                var bleed = (entry.Bleed ?? System.Numerics.Vector3.One);

                return new Double3(X: ((entry.Albedo.X * diffuse) * bleed.X), Y: ((entry.Albedo.Y * diffuse) * bleed.Y), Z: ((entry.Albedo.Z * diffuse) * bleed.Z));
            },
            direct: direct,
            emission: material => {
                var entry = materials[material];
                var bleed = (entry.Bleed ?? System.Numerics.Vector3.One);

                return new Double3(X: ((entry.Albedo.X * entry.Emissive) * bleed.X), Y: ((entry.Albedo.Y * entry.Emissive) * bleed.Y), Z: ((entry.Albedo.Z * entry.Emissive) * bleed.Z));
            },
            sky: sky,
            screens: screens,
            reflection: reflection
        );
    }
}
