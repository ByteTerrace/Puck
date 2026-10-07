using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: what the value-domain guard keeps for a binding, the last value it presented from a valid
/// input and whether it has reported the binding invalid, lives exactly as long as the binding does in the world it
/// belongs to. A binding that is removed and added again starts fresh, whatever else still reads its row; a world that
/// is replaced, or whose timeline is restored, starts every binding fresh; and a document that renames what it binds
/// does not make the guard accumulate what it bound before.
/// </summary>
public sealed class WorldValueDomainLifetimeLawTests {
    private static readonly SdfAnchor Origin = new(
        Orientation: Quaternion.Identity,
        Position: Vector3.Zero
    );

    private static BindableScalar Bound => new(binding: $"state.{WorldValueDomainLawTests.Row}");

    private static WorldDefinition Clouds(bool scaleBound, double value) => WorldValueDomainLawTests.WithRow(
        definition: WorldValueDomainLawTests.Layer(layer: new WorldRenderSkyLayer.Clouds(
            Coverage: Bound,
            Scale: (scaleBound ? Bound : null)
        )),
        value: value
    );
    private static WorldDefinition FieldOfView(string program, double value) => WorldValueDomainLawTests.WithRow(
        definition: Fixtures.BuildDocument() with {
            CamerasRaw = [
                new WorldCamera(
                    Anchor: null,
                    Name: "probe",
                    RenderHeight: 240u,
                    RenderWidth: 320u,
                    Rig: new WorldCameraProgram(
                        Name: program,
                        Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: Bound)],
                        Version: WorldCameraProgram.CurrentVersion
                    )
                ),
            ],
        },
        value: value
    );
    private static float Fov(WorldDefinition definition, WorldStateMirror mirror, WorldValueDomainGuard domains, int camera = 0) => WorldCameraRigCompiler.Compile(
        definition: definition,
        domains: domains,
        mirror: mirror,
        program: definition.Cameras[camera].Rig
    ).Resolve(
        anchor: in Origin,
        clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f)
    ).FovRadians;

    [Fact]
    public void A_binding_removed_and_added_again_starts_fresh_while_another_field_still_reads_its_row() {
        var domains = new WorldValueDomainGuard();
        var reports = new List<string>();
        var resolve = new WorldEnvironmentResolve(domains: domains);
        var world = new WorldValueDomainLawTests.LiveWorld(definition: Clouds(scaleBound: true, value: 0.5d));
        var engineDefault = resolve.Resolve(definition: Clouds(scaleBound: false, value: 0.5d), mirror: world.Mirror, revision: 0).Sky.First<SdfSkyClouds>().Scale;

        domains.Report = reports.Add;

        Assert.Equal(expected: 0.5f, actual: resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: 1).Sky.First<SdfSkyClouds>().Scale);

        // The row goes to 0: coverage (closed, so 0 is inside) takes it, scale (open at 0) holds 0.5 and is reported.
        world.Set(definition: Clouds(scaleBound: true, value: 0d));

        Assert.Equal(expected: 0.5f, actual: resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: 2).Sky.First<SdfSkyClouds>().Scale);
        Assert.Single(collection: reports);

        // The scale binding goes away while coverage keeps reading the row, and a frame is presented without it.
        world.Set(definition: Clouds(scaleBound: false, value: 0d));
        _ = resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: 3);

        // It comes back with the row still at 0: a new binding, which holds nothing from the old one.
        world.Set(definition: Clouds(scaleBound: true, value: 0d));

        Assert.NotEqual(expected: 0.5f, actual: resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: 4).Sky.First<SdfSkyClouds>().Scale);
        Assert.Equal(expected: engineDefault, actual: resolve.Resolve(definition: world.Current, mirror: world.Mirror, revision: 5).Sky.First<SdfSkyClouds>().Scale);
        Assert.Equal(expected: 2, actual: reports.Count);
    }
    [Fact]
    public void Renaming_what_a_camera_program_binds_does_not_accumulate_what_it_bound_before() {
        var domains = new WorldValueDomainGuard();
        var world = new WorldValueDomainLawTests.LiveWorld(definition: FieldOfView(program: "rig-seed", value: 0.5d));

        domains.Report = static _ => { };

        for (var generation = 0; (generation < 30); generation++) {
            // The program is renamed and the row written outside the field's domain; the guard keeps no entry per name.
            world.Set(definition: FieldOfView(program: $"rig{generation}", value: 4d));
            _ = Fov(definition: world.Current, mirror: world.Mirror, domains: domains);

            Assert.True(condition: (domains.Tracked <= 2), userMessage: $"rig{generation}: the guard tracks {domains.Tracked} bindings");
        }

        Assert.True(condition: (domains.Tracked >= 1));
    }
    [Fact]
    public void A_replaced_world_starts_every_binding_fresh_and_an_ordinary_update_keeps_its_history() {
        var domains = new WorldValueDomainGuard();
        var reports = new List<string>();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var a = FieldOfView(program: "probe-rig", value: 0.5d);
        var client = ClientFixtures.Client(definition: a);

        domains.Report = reports.Add;
        client.DeliverDefinition(definition: a, version: new WorldDocumentVersion(Activation: first, Sequence: 0L));

        Assert.Equal(expected: 0.5f, actual: Fov(definition: a, mirror: client.StateMirror, domains: domains));

        // An ordinary update of the same world keeps what the binding last presented from a valid input.
        var written = FieldOfView(program: "probe-rig", value: 4d);

        client.DeliverDefinition(definition: written, version: new WorldDocumentVersion(Activation: first, Sequence: 1L));

        Assert.Equal(expected: 0.5f, actual: Fov(definition: written, mirror: client.StateMirror, domains: domains));

        // A different world, with the same program, site and binding, whose first value is out of range: it has no
        // valid value of its own yet, so it presents the field's engine default, not the other world's 0.5.
        var b = FieldOfView(program: "probe-rig", value: 4d);

        client.DeliverDefinition(definition: b, version: new WorldDocumentVersion(Activation: second, Sequence: 0L));

        Assert.Equal(expected: OrbitRig.DefaultFieldOfViewRadians, actual: Fov(definition: b, mirror: client.StateMirror, domains: domains));
        Assert.Equal(expected: 2, actual: reports.Count);
    }
    [Fact]
    public void A_restored_timeline_starts_every_binding_fresh() {
        var domains = new WorldValueDomainGuard();
        var a = FieldOfView(program: "probe-rig", value: 0.5d);
        var world = new WorldValueDomainLawTests.LiveWorld(definition: a);

        Assert.Equal(expected: 0.5f, actual: Fov(definition: a, mirror: world.Mirror, domains: domains));

        world.Set(definition: FieldOfView(program: "probe-rig", value: 4d));

        Assert.Equal(expected: 0.5f, actual: Fov(definition: world.Current, mirror: world.Mirror, domains: domains));

        domains.Restart(mirror: world.Mirror);

        Assert.Equal(expected: 0, actual: domains.Tracked);
        Assert.Equal(expected: OrbitRig.DefaultFieldOfViewRadians, actual: Fov(definition: world.Current, mirror: world.Mirror, domains: domains));
    }
    [Fact]
    public void A_camera_that_is_hidden_keeps_its_history_for_as_long_as_it_is_authored() {
        static WorldCamera Camera(string name) => new(
            Anchor: null,
            Name: name,
            RenderHeight: 240u,
            RenderWidth: 320u,
            Rig: new WorldCameraProgram(
                Name: $"{name}-rig",
                Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: Bound)],
                Version: WorldCameraProgram.CurrentVersion
            )
        );
        static WorldDefinition Two(double value) => WorldValueDomainLawTests.WithRow(
            definition: Fixtures.BuildDocument() with { CamerasRaw = [Camera(name: "a"), Camera(name: "b")] },
            value: value
        );

        var domains = new WorldValueDomainGuard();
        var world = new WorldValueDomainLawTests.LiveWorld(definition: Two(value: 0.5d));

        domains.Report = static _ => { };

        Assert.Equal(expected: 0.5f, actual: Fov(definition: world.Current, mirror: world.Mirror, domains: domains, camera: 0));

        // Camera A is hidden: nothing resolves it while unrelated installs pass and the other camera keeps resolving.
        world.Set(definition: Two(value: 0.5d));
        Assert.Equal(expected: 0.5f, actual: Fov(definition: world.Current, mirror: world.Mirror, domains: domains, camera: 1));
        world.Set(definition: Two(value: 0.5d));
        Assert.Equal(expected: 0.5f, actual: Fov(definition: world.Current, mirror: world.Mirror, domains: domains, camera: 1));

        // Its row goes out of range while it is hidden, and it is shown again: still authored, so it holds what it had.
        world.Set(definition: Two(value: 4d));

        Assert.Equal(expected: 0.5f, actual: Fov(definition: world.Current, mirror: world.Mirror, domains: domains, camera: 0));
    }
    [Fact]
    public void A_restored_timeline_starts_only_its_own_world_fresh() {
        var domains = new WorldValueDomainGuard();
        var restored = new WorldValueDomainLawTests.LiveWorld(definition: FieldOfView(program: "probe-rig", value: 0.5d));
        var other = new WorldValueDomainLawTests.LiveWorld(definition: FieldOfView(program: "probe-rig", value: 0.5d));

        domains.Report = static _ => { };

        Assert.Equal(expected: 0.5f, actual: Fov(definition: restored.Current, mirror: restored.Mirror, domains: domains));
        Assert.Equal(expected: 0.5f, actual: Fov(definition: other.Current, mirror: other.Mirror, domains: domains));

        restored.Set(definition: FieldOfView(program: "probe-rig", value: 4d));
        other.Set(definition: FieldOfView(program: "probe-rig", value: 4d));

        domains.Restart(mirror: restored.Mirror);

        // The restored world has no history; the independent one keeps its held value.
        Assert.Equal(expected: OrbitRig.DefaultFieldOfViewRadians, actual: Fov(definition: restored.Current, mirror: restored.Mirror, domains: domains));
        Assert.Equal(expected: 0.5f, actual: Fov(definition: other.Current, mirror: other.Mirror, domains: domains));
    }
}
