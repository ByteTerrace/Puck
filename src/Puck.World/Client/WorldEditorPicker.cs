using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Queries;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>One selectable document row in the pick/candidate table: the section + stable key that becomes the
/// selection, and the row's authored focus position (the proximity-sort key and the orbit pivot seed).</summary>
/// <param name="Section">The world-document section the row lives in.</param>
/// <param name="Id">The row's stable string id (empty for a screen — screens key by <paramref name="Index"/>).</param>
/// <param name="Index">The engine screen index (<c>-1</c> for every non-screen row).</param>
/// <param name="Focus">The row's authored position (a scene row's center, a screen's face origin, a spawn/camera point).</param>
internal readonly record struct EditorPickTarget(WorldSection Section, string Id, int Index, Vector3 Focus);

/// <summary>
/// The editor's look-ray picking program: a fixed-point SDF program derived from the DOCUMENT (never the live render
/// program, whose <c>TransformDynamic</c> avatar leaves <see cref="SdfFieldEvaluator"/> rejects by contract) with ONE
/// MATERIAL PER SELECTABLE ROW, so a <see cref="RayHit.Material"/> IS the row's index into the parallel
/// <see cref="Targets"/> table. Scene rows and screens march their real geometry; spawn points and fixed cameras march
/// small proxy spheres (they have no authored volume); anchored cameras carry no proxy (their live pose is not document
/// data — select them by name over the console twin). Material 0 is the non-selectable ground sentinel, so a ray that
/// falls to grass terminates fast and picks nothing. Rebuilt lazily when <see cref="WorldClient.DefinitionRevision"/>
/// moves; a cast costs microseconds against these row counts.
/// </summary>
internal sealed class WorldEditorPicker {
    private const float SpawnProxyRadius = 0.4f;
    private const float CameraProxyRadius = 0.3f;
    private const float SpeakerProxyRadius = 0.3f;
    private static readonly FixedQ4816 MaxPickDistance = FixedQ4816.FromInteger(value: 256L);

    private readonly WorldClient m_client;
    private EditorPickTarget[] m_targets = [];
    private SdfFieldEvaluator? m_evaluator;
    private int m_builtRevision = -1;

    /// <summary>Initializes a new instance of the <see cref="WorldEditorPicker"/> class.</summary>
    /// <param name="client">The client view whose delivered definition the picking program derives from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public WorldEditorPicker(WorldClient client) {
        ArgumentNullException.ThrowIfNull(argument: client);

        m_client = client;
    }

    /// <summary>The selectable rows of the current definition, in pick-table order (the proximity candidate pool).</summary>
    public ReadOnlySpan<EditorPickTarget> Targets {
        get {
            EnsureBuilt();

            return m_targets;
        }
    }

    /// <summary>Casts the editor camera's look ray against the picking program and resolves the nearest selectable row.</summary>
    /// <param name="eye">The ray origin (the editor camera's eye), world space.</param>
    /// <param name="direction">The look direction (need not be unit length).</param>
    /// <param name="target">The picked row, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the ray hit a selectable row within the pick range.</returns>
    public bool TryPick(Vector3 eye, Vector3 direction, out EditorPickTarget target) {
        target = default;
        EnsureBuilt();

        if ((m_evaluator is not { } evaluator) || (direction.LengthSquared() < 1e-8f)) {
            return false;
        }

        if (!evaluator.Raycast(origin: WorldCoord3.FromLocal(local: ToFixed(value: eye)), dir: ToFixed(value: direction), maxDist: MaxPickDistance, hit: out var hit)) {
            return false;
        }

        // Material 0 is the ground sentinel; every selectable row's material is its table index + 1.
        var index = (hit.Material - 1);

        if ((uint)index >= (uint)m_targets.Length) {
            return false;
        }

        target = m_targets[index];

        return true;
    }

    // Rebuild the program + table only when a definition delivery landed (mutation, swap, undo).
    private void EnsureBuilt() {
        var revision = m_client.DefinitionRevision;

        if (revision == m_builtRevision) {
            return;
        }

        Build();
        m_builtRevision = revision;
    }

    // The document walk: plain unions (picking wants crisp per-row distances, not the render's smooth melds — the
    // centers match, which is all a selection needs), one throwaway albedo per material (the evaluator never shades).
    private void Build() {
        var definition = m_client.Definition;
        var builder = new SdfProgramBuilder();
        var targets = new List<EditorPickTarget>();
        // Material 0: the ground sentinel (AddMaterial returns sequential indices, so row material == targets.Count + 1).
        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)));

        foreach (var row in definition.Scene.Rows) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.Translate(offset: row.Center);
            _ = (row switch {
                WorldSceneRow.Boulder boulder => builder.Sphere(radius: boulder.Radius, material: material),
                WorldSceneRow.Slab slab => builder.Box(halfExtents: slab.HalfExtents, round: slab.Round, material: material),
                _ => builder,
            });
            _ = builder.ResetPoint();
            targets.Add(item: new EditorPickTarget(Section: WorldSection.Scene, Id: row.Id, Index: -1, Focus: row.Center));
        }

        foreach (var screen in definition.Screens) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            // The same center derivation the frame source bakes: the geometry box sits one HalfDepth behind the face
            // along the face normal. World screens keep world-axis Right/Up (the render only translates slabs), so an
            // axis-aligned box matches the drawn slab.
            var normal = Vector3.Normalize(value: Vector3.Cross(vector1: screen.Right, vector2: screen.Up));
            var center = (screen.Origin - (normal * screen.HalfDepth));

            _ = builder
                .Translate(offset: center)
                .Box(halfExtents: new Vector3(x: screen.HalfWidth, y: screen.HalfHeight, z: screen.HalfDepth), round: screen.Round, material: material)
                .ResetPoint();
            targets.Add(item: new EditorPickTarget(Section: WorldSection.Screens, Id: string.Empty, Index: screen.Index, Focus: screen.Origin));
        }

        // Placements pick by a reach-sized proxy sphere at the stamp's position — the creation's own geometry would
        // demand a per-shape replay in fixed point for little gain; the proxy covers the stamp's core mass.
        foreach (var placement in definition.Placements) {
            if (WorldPlacementStamper.FindCreation(creations: definition.Creations, id: placement.CreationId) is not { } creation) {
                continue;
            }

            var radius = MathF.Max(x: (Puck.Authoring.CreationGeometry.Reach(document: creation.Document) * (placement.Scale * 0.5f)), y: 0.35f);

            AddProxy(
                builder: builder,
                targets: targets,
                target: new EditorPickTarget(Section: WorldSection.Placements, Id: placement.Id, Index: -1, Focus: placement.Position),
                center: (placement.Position + new Vector3(x: 0f, y: radius, z: 0f)),
                radius: radius
            );
        }

        foreach (var spawn in definition.SpawnPoints) {
            AddProxy(builder: builder, targets: targets, target: new EditorPickTarget(Section: WorldSection.Spawns, Id: spawn.Id, Index: -1, Focus: spawn.Position), center: (spawn.Position + new Vector3(x: 0f, y: SpawnProxyRadius, z: 0f)), radius: SpawnProxyRadius);
        }

        foreach (var camera in definition.Cameras) {
            if (camera is WorldCamera.Fixed fixedCamera) {
                AddProxy(builder: builder, targets: targets, target: new EditorPickTarget(Section: WorldSection.Cameras, Id: camera.Name, Index: -1, Focus: fixedCamera.Position), center: fixedCamera.Position, radius: CameraProxyRadius);
            }
        }

        // Speakers have no geometry: Fixed and Bed rows pick by a small proxy sphere at their authored point (the
        // same proxy-sphere approach fixed cameras use); an ANCHORED row's live pose is not document data — select it by name over the
        // console twin (editor.select speakers <name>), like anchored cameras.
        foreach (var speaker in definition.Speakers) {
            switch (speaker) {
                case WorldSpeaker.Fixed fixedSpeaker:
                    AddProxy(builder: builder, targets: targets, target: new EditorPickTarget(Section: WorldSection.Speakers, Id: speaker.Name, Index: -1, Focus: fixedSpeaker.Position), center: fixedSpeaker.Position, radius: SpeakerProxyRadius);

                    break;
                case WorldSpeaker.Bed bed:
                    AddProxy(builder: builder, targets: targets, target: new EditorPickTarget(Section: WorldSection.Speakers, Id: speaker.Name, Index: -1, Focus: bed.Center), center: bed.Center, radius: SpeakerProxyRadius);

                    break;
            }
        }

        m_evaluator = new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        m_targets = [.. targets];
    }

    private static void AddProxy(SdfProgramBuilder builder, List<EditorPickTarget> targets, in EditorPickTarget target, Vector3 center, float radius) {
        _ = builder
            .Translate(offset: center)
            .Sphere(radius: radius, material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)))
            .ResetPoint();
        targets.Add(item: target);
    }

    private static FixedVector3 ToFixed(Vector3 value) => new(
        X: FixedQ4816.FromDouble(value: value.X),
        Y: FixedQ4816.FromDouble(value: value.Y),
        Z: FixedQ4816.FromDouble(value: value.Z)
    );
}
