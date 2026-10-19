using System.Numerics;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The walk the mesh bake samples a group through answers that group alone. The per-instance probe
/// (<c>Assets/Shaders/sdf-instance-field.comp.hlsl</c>) runs <c>mapCore</c> with one instance in its mask and no world
/// segment, as <c>sdf-mesh-bake.comp.hlsl</c> does, over programs of several instances: static instances the rigid plans
/// carry, static parts two of which share an asset, dynamic parts posed through the dynamic transforms, and beside them an
/// instance whose unscoped warp lowers the program's step scale. At every point of the lattice the answer, with the step
/// scale divided out, is the evaluator's over a program holding the selected instance alone, posed by translation and
/// rotation where the device poses it by its slot: its distance within <see cref="Tolerance"/>, its material, and the
/// selected instance as the answering one. The red leg selects every instance, and must miss the selected one's reference
/// somewhere, so the law sees another instance's answer.
/// </summary>
public sealed partial class SdfFieldDeviceLawTests {
    private const string InstanceKernelName = "sdf-instance-field.comp";
    // The words one leg's mask entry takes, the probe's FieldMaskEntryWords: one primary word, then its summary word.
    private const int MaskEntryWords = 2;

    // The dynamic parts' slots: each a position and an orientation, the rows TransformDynamic reads, and a lanes row.
    private static readonly (Vector3 Position, Quaternion Orientation)[] Slots = [
        (new Vector3(x: -0.45f, y: 0.2f, z: 0.1f), Quaternion.CreateFromYawPitchRoll(yaw: 0.6f, pitch: 0.2f, roll: -0.3f)),
        (new Vector3(x: 0.5f, y: -0.15f, z: -0.2f), Quaternion.CreateFromYawPitchRoll(yaw: -0.4f, pitch: 0.5f, roll: 0.1f)),
        (new Vector3(x: 0.05f, y: 0.45f, z: 0.55f), Quaternion.CreateFromYawPitchRoll(yaw: 1.1f, pitch: -0.2f, roll: 0.4f)),
    ];

    [Fact]
    public void AVulkanDeviceAnswersAMaskedGroupAsItsInstancesAloneDo() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfFieldDeviceLawTests));

        VerifyInstances(
            backend: $"vulkan ({device.Name})",
            kernel: Kernel(extension: ".spv", name: InstanceKernelName),
            services: device.Services
        );
    }
    [Fact]
    public void ADirect3D12DeviceAnswersAMaskedGroupAsItsInstancesAloneDo() {
        using (var context = DirectXTestDevices.Hardware()) {
            VerifyInstances(
                backend: "directx",
                kernel: Kernel(extension: ".dxil", name: InstanceKernelName),
                services: context.Services
            );
        }
    }

    private static void VerifyInstances(string backend, byte[] kernel, Puck.Abstractions.Gpu.GpuDeviceServices services) {
        var legs = InstanceLegs();
        var masks = new uint[(legs.Count * MaskEntryWords)];

        for (var index = 0; (index < legs.Count); index++) {
            masks[(index * MaskEntryWords)] = legs[index].Mask;
            masks[((index * MaskEntryWords) + 1)] = ((legs[index].Mask != 0U) ? 1U : 0U);
        }

        var results = Run(
            kernel: kernel,
            legs: [.. legs.Select(selector: static leg => leg.Field)],
            masks: masks,
            name: InstanceKernelName,
            services: services,
            transforms: Transforms()
        );
        var failures = new List<string>();

        for (var index = 0; (index < legs.Count); index++) {
            var leg = legs[index];
            var worst = 0f;
            var worstPoint = 0;
            var materialMisses = 0;
            var instanceMisses = 0;
            var stepScale = 1f;

            for (var point = 0; (point < Points.Length); point++) {
                var result = results[((index * Points.Length) + point)];
                var miss = MathF.Abs(x: ((result.X / result.W) - leg.Field.Distances[point]));

                stepScale = result.W;
                if (!(miss <= worst)) {
                    worst = miss;
                    worstPoint = point;
                }
                if (BitConverter.SingleToInt32Bits(value: result.Y) != leg.Field.Materials[point]) {
                    materialMisses++;
                }
                if (BitConverter.SingleToInt32Bits(value: result.Z) != leg.Selected) {
                    instanceMisses++;
                }
            }

            if (leg.Field.Swapped) {
                if (worst <= Tolerance) {
                    failures.Add(item: $"{leg.Field.Name}: every instance in the mask stayed within {Tolerance} of instance {leg.Selected} alone everywhere (worst {worst})");
                }
            } else if (!(worst <= Tolerance) || (materialMisses != 0) || (instanceMisses != 0)) {
                failures.Add(item: $"{leg.Field.Name}: worst distance miss {worst} at {Points[worstPoint]} (reference {leg.Field.Distances[worstPoint]}), {materialMisses} material miss(es), {instanceMisses} answer(s) from another instance");
            }
            if (leg.Warped && !(stepScale < 1f)) {
                failures.Add(item: $"{leg.Field.Name}: the warped instance left the program's step scale at {stepScale}");
            }
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: $"{backend}: {failures.Count} of {legs.Count} masked walk(s) disagree with their instance alone:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: failures)}"
        );
    }
    // The dynamic transforms: three rows a slot, its position, its orientation and its lanes.
    private static Vector4[] Transforms() => [.. Slots.SelectMany(selector: static slot => new[] {
        new Vector4(value: slot.Position, w: 0f),
        new Vector4(x: slot.Orientation.X, y: slot.Orientation.Y, z: slot.Orientation.Z, w: slot.Orientation.W),
        Vector4.Zero,
    })];
    // Every leg: each instance of each program selected alone, then each program with every instance selected as its
    // red leg against its first instance.
    private static List<SdfInstanceLeg> InstanceLegs() {
        var legs = new List<SdfInstanceLeg>();

        foreach (var (name, instances, warped) in InstancePrograms()) {
            var device = BuildInstances(instances: instances, posed: false, selected: null);

            for (var selected = 0; (selected < instances.Length); selected++) {
                if (instances[selected].Refused) {
                    continue;
                }

                var reference = Evaluator(program: BuildInstances(instances: instances, posed: true, selected: selected))
                    ?? throw new InvalidOperationException(message: $"{name}: the evaluator refuses instance {selected} alone.");
                var (distances, materials) = Reference(evaluator: reference);

                legs.Add(item: new SdfInstanceLeg(
                    Field: new SdfFieldLeg(Distances: distances, Materials: materials, Name: $"{name} instance {selected}", Words: device.Words.ToArray()),
                    Mask: (1U << selected),
                    Selected: selected,
                    Warped: warped
                ));
            }

            var first = legs[^instances.Count(predicate: static instance => !instance.Refused)];

            legs.Add(item: first with {
                Field = first.Field with { Name = $"{name} every instance", Swapped = true },
                Mask = ((1U << instances.Length) - 1U),
            });
        }

        return legs;
    }
    // The programs: each instance emits itself into a builder with a material, posed either by its dynamic slot (the
    // device's program) or by the translation and rotation that slot holds (the reference's).
    private static IEnumerable<(string Name, SdfInstanceSpec[] Instances, bool Warped)> InstancePrograms() {
        var plate = new Vector3(x: 0.35f, y: 0.3f, z: 0.25f);

        yield return ("rigid", [
            Static(center: new Vector3(x: -0.45f, y: 0f, z: 0f), radius: 0.6f, emit: (b, m) => b.Translate(offset: new Vector3(x: -0.45f, y: 0f, z: 0f)).Rotate(rotation: Slots[0].Orientation).Box(halfExtents: plate, material: m, round: 0.05f)),
            Static(center: new Vector3(x: 0.45f, y: 0.1f, z: 0f), radius: 0.45f, emit: (b, m) => b.Translate(offset: new Vector3(x: 0.45f, y: 0.1f, z: 0f)).Sphere(material: m, radius: 0.4f)),
            Static(center: new Vector3(x: 0f, y: 0.55f, z: 0.3f), radius: 0.6f, emit: (b, m) => b.Translate(offset: new Vector3(x: 0f, y: 0.55f, z: 0.3f)).Rotate(rotation: Slots[1].Orientation).Box(halfExtents: new Vector3(x: 0.5f, y: 0.1f, z: 0.2f), material: m, round: 0.02f)),
        ], false);
        yield return ("static-parts", [
            Part(center: Vector3.Zero, radius: 0.6f, emit: (b, m) => b.Box(halfExtents: plate, material: m, round: 0.05f)),
            Part(center: Vector3.Zero, radius: 0.65f, emit: (b, m) => b.Sphere(material: m, radius: 0.6f)),
            Part(center: Vector3.Zero, radius: 0.6f, emit: (b, m) => b.Box(halfExtents: plate, material: m, round: 0.05f)),
        ], false);
        yield return ("dynamic-parts", [
            Dynamic(slot: 0, radius: 0.6f, emit: (b, m) => b.Box(halfExtents: plate, material: m, round: 0.05f)),
            Dynamic(slot: 1, radius: 0.6f, emit: (b, m) => b.Box(halfExtents: plate, material: m, round: 0.05f)),
            Dynamic(slot: 2, radius: 0.5f, emit: (b, m) => b.Sphere(material: m, radius: 0.45f)),
        ], false);
        yield return ("beside-a-warp", [
            Static(center: new Vector3(x: -0.45f, y: 0f, z: 0f), radius: 0.6f, emit: (b, m) => b.Translate(offset: new Vector3(x: -0.45f, y: 0f, z: 0f)).Box(halfExtents: plate, material: m, round: 0.05f)),
            Dynamic(slot: 1, radius: 0.6f, emit: (b, m) => b.Box(halfExtents: plate, material: m, round: 0.05f)),
            new SdfInstanceSpec(
                Emit: (b, m, _) => b.BeginInstance(boundCenter: new Vector3(x: 0.3f, y: -0.6f, z: 0f), boundRadius: 0.7f).ResetPoint()
                    .Translate(offset: new Vector3(x: 0.3f, y: -0.6f, z: 0f)).DomainWarp(amplitude: 0.12f, frequency: new Vector3(value: 2.5f))
                    .Box(halfExtents: plate, material: m, round: 0.05f).EndInstance(),
                Refused: true
            ),
        ], true);
    }
    private static SdfInstanceSpec Static(Vector3 center, float radius, Func<SdfProgramBuilder, int, SdfProgramBuilder> emit) => new(
        Emit: (b, m, _) => emit(arg1: b.BeginInstance(boundCenter: center, boundRadius: radius).ResetPoint(), arg2: m).EndInstance()
    );
    // A part: a whole instance that is one field scope of reset chains, which the program compiles to a part program.
    private static SdfInstanceSpec Part(Vector3 center, float radius, Func<SdfProgramBuilder, int, SdfProgramBuilder> emit) => new(
        Emit: (b, m, _) => emit(arg1: b.BeginInstance(boundCenter: center, boundRadius: radius).PushField().ResetPoint(), arg2: m).PopField().EndInstance()
    );
    // A dynamic part riding a slot: on the device the slot's transform poses it; in the reference, the translation and
    // rotation the slot holds, applied as TransformDynamic applies them.
    private static SdfInstanceSpec Dynamic(int slot, float radius, Func<SdfProgramBuilder, int, SdfProgramBuilder> emit) => new(
        Emit: (b, m, posed) => (posed
            ? emit(arg1: b.BeginInstance(boundCenter: Slots[slot].Position, boundRadius: radius).PushField().ResetPoint()
                .Translate(offset: Slots[slot].Position).Rotate(rotation: Slots[slot].Orientation), arg2: m).PopField().EndInstance()
            : b.DynamicInstance(boundOffset: Vector3.Zero, boundRadius: radius, slot: slot,
                emit: instance => emit(arg1: instance.PushField().ResetPoint().TransformDynamic(slot: slot), arg2: m).PopField()))
    );
    // A program of the instances, each with a material of its own, or of the selected one alone with the material it has
    // in the whole program.
    private static SdfProgram BuildInstances(SdfInstanceSpec[] instances, bool posed, int? selected) {
        var builder = new SdfProgramBuilder();
        var materials = instances.Select(selector: (_, index) => builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: (index + 1) * 0.25f, y: 0.5f, z: 0.5f)))).ToArray();

        for (var index = 0; (index < instances.Length); index++) {
            if ((selected is null) || (selected == index)) {
                _ = instances[index].Emit(arg1: builder, arg2: materials[index], arg3: posed);
            }
        }

        return builder.Build();
    }

    // One instance of a program: how it emits itself, and whether the evaluator refuses it alone.
    private sealed record SdfInstanceSpec(Func<SdfProgramBuilder, int, bool, SdfProgramBuilder> Emit, bool Refused = false);
    // One masked walk: the program and the reference of its selected instance alone, the mask, and whether the program
    // carries a warp that lowers its step scale.
    private sealed record SdfInstanceLeg(SdfFieldLeg Field, uint Mask, int Selected, bool Warped);
}
