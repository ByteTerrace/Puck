using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>
/// The scene a far view of a world composes: that world's far product (<see cref="SdfFarProduct"/>) and nothing else. It
/// emits the product's materials and no shape, so the residency that draws it plans no bake group and bakes nothing,
/// and it publishes one static draw per piece of the product's mesh. A product never changes under its emitter: a world
/// whose product is built again gets a new emitter, and so a new scene, which keeps every program this emitter's frames
/// carry holding the materials its draws name.
/// </summary>
public sealed class WorldFarProductEmitter : ISdfSceneEmitter {
    private SdfMeshDraw[] m_draws = [];
    private int m_materialBase = -1;

    /// <summary>Initializes a new instance of the <see cref="WorldFarProductEmitter"/> class.</summary>
    /// <param name="product">The product the scene draws.</param>
    /// <exception cref="ArgumentNullException"><paramref name="product"/> is <see langword="null"/>.</exception>
    public WorldFarProductEmitter(SdfFarProduct product) {
        ArgumentNullException.ThrowIfNull(argument: product);
        Product = product;
    }

    /// <summary>Gets the product the scene draws.</summary>
    public SdfFarProduct Product { get; }
    /// <inheritdoc/>
    public IReadOnlyList<SdfMeshDraw> MeshDraws => m_draws;
    /// <inheritdoc/>
    public long MeshDrawsRevision { get; private set; }

    /// <inheritdoc/>
    /// <remarks>Adds the product's materials. The draws' material base is the builder's material count before them, the
    /// same on every emission of one composition, so the draws are made at the first emission and kept.</remarks>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        var materialBase = builder.MaterialCount;

        foreach (var material in Product.Materials) { _ = builder.AddMaterial(material: material); }
        if (materialBase != m_materialBase) {
            m_materialBase = materialBase;
            m_draws = Product.DrawsAt(identity: this, materialBase: materialBase);
            MeshDrawsRevision++;
        }
    }
}
