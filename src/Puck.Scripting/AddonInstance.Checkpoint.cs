using System.Text.Json;
using System.Text.Json.Serialization;
using Wasmtime;

namespace Puck.Scripting;

public sealed partial class AddonInstance {
    private readonly string[]? m_checkpointGlobals;
    private readonly string? m_checkpointRefusal;

    private Instance? m_instance;

    private sealed record GlobalImage(ValueKind Kind, long Bits);
    private sealed record Image(string? Hash, byte[] Memory, long Pages, GlobalImage[] Globals,
        AddonState State, AddonFault Fault, bool Admitted, ulong Fuel, ulong LastFuel);
    [JsonSerializable(typeof(Image))]
    [JsonSourceGenerationOptions(Converters = [typeof(Puck.Abstractions.Documents.StrictEnumConverter<ValueKind>),
        typeof(Puck.Abstractions.Documents.StrictEnumConverter<AddonState>), typeof(Puck.Abstractions.Documents.StrictEnumConverter<AddonFaultKind>)])]
    private sealed partial class CheckpointJsonContext : JsonSerializerContext;

    /// <summary>Captures the complete import-free guest between calls, including private numeric globals, grown
    /// memory, fuel and sticky faults. Refuses resources whose state the native API cannot reconstruct.</summary>
    /// <returns>The guest's opaque checkpoint.</returns>
    public byte[] CaptureCheckpoint() {
        if ((m_checkpointGlobals is null) || (m_checkpointRefusal is not null) || (m_memory is null) || (m_store is null)) {
            throw new InvalidOperationException(message: $"addon '{Name}': {(m_checkpointRefusal ?? "checkpoint exports or a live store are unavailable")}");
        }
        var globals = m_checkpointGlobals.Select(selector: name => {
            var global = m_instance!.GetGlobal(name: name)!;
            var bits = global.GetValue() switch {
                int value => value,
                long value => value,
                float value => BitConverter.SingleToInt32Bits(value: value),
                double value => BitConverter.DoubleToInt64Bits(value: value),
                _ => throw new InvalidOperationException(message: $"addon '{Name}': reference global '{name}' cannot be checkpointed"),
            };

            return new GlobalImage(global.Kind, bits);
        }).ToArray();

        return JsonSerializer.SerializeToUtf8Bytes(new Image(m_hash?.ToString(), m_memory.GetSpan(address: 0, length: checked((int)m_memory.GetLength())).ToArray(),
            m_memory.GetSize(), globals, m_state, m_fault, m_admitted, m_store.Fuel, m_lastFuelConsumed), CheckpointJsonContext.Default.Image);
    }
    /// <summary>Restores a captured guest into a freshly instantiated and admitted copy of the same pinned module.</summary>
    /// <param name="checkpoint">The opaque image captured by this implementation.</param>
    public void RestoreCheckpoint(byte[] checkpoint) {
        var image = (JsonSerializer.Deserialize(checkpoint, CheckpointJsonContext.Default.Image) ?? throw new InvalidDataException(message: "Missing guest checkpoint."));

        if ((image.Hash != m_hash?.ToString()) || (m_checkpointGlobals is null) ||
            (image.Globals.Length != m_checkpointGlobals.Length) || (m_memory is null) || (m_store is null) ||
            (image.Memory.Length > AddonModuleLimits.MemoryBytes) || !Enum.IsDefined(image.State) || !Enum.IsDefined(image.Fault.Kind)) {
            throw new InvalidDataException(message: $"addon '{Name}': checkpoint module or resource inventory differs");
        }
        var pages = m_memory.GetSize();

        if (image.Pages < pages) { throw new InvalidDataException(message: "Guest restore requires a fresh store; memory cannot shrink."); }
        if (image.Pages > pages) { m_memory.Grow((image.Pages - pages)); }
        if (image.Memory.Length != m_memory.GetLength()) { throw new InvalidDataException(message: "Guest checkpoint memory size differs."); }
        for (var index = 0; (index < image.Globals.Length); index++) {
            var global = m_instance!.GetGlobal(name: m_checkpointGlobals[index])!;
            var saved = image.Globals[index];

            if (saved.Kind != global.Kind) { throw new InvalidDataException(message: "Guest checkpoint global type differs."); }
            if (global.Mutability != Mutability.Mutable) { continue; }
            var value = saved.Kind switch {
                ValueKind.Int32 => ((object)unchecked((int)saved.Bits)),
                ValueKind.Int64 => saved.Bits,
                ValueKind.Float32 => BitConverter.Int32BitsToSingle(value: unchecked((int)saved.Bits)),
                ValueKind.Float64 => BitConverter.Int64BitsToDouble(saved.Bits),
                _ => throw new InvalidDataException(message: "Unsupported checkpoint global type."),
            };

            global.SetValue(value);
        }
        image.Memory.CopyTo(m_memory.GetSpan(0, image.Memory.Length));
        m_state = image.State; m_fault = image.Fault; m_admitted = image.Admitted;
        m_store.Fuel = image.Fuel; m_lastFuelConsumed = image.LastFuel; m_lastCount = 0;
    }
}
