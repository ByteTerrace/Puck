using System.Text.Json;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>One bus-pin sample from a vector's <c>cycles</c> array: an address/data pair sampled between M-states,
/// tagged with which pins were active. <see cref="IsRead"/>/<see cref="IsWrite"/> are both <see langword="false"/> for
/// a purely internal M-cycle (flags <c>"---"</c>), which the SM83 core never surfaces as a bus call — only read/write
/// entries are comparable against <see cref="Sm83SstBus.Accesses"/>.</summary>
/// <param name="Address">The address pins, or <see langword="null"/> when electrically disconnected.</param>
/// <param name="Data">The data pins, or <see langword="null"/> when electrically disconnected.</param>
/// <param name="IsRead">Whether the read pin was asserted.</param>
/// <param name="IsWrite">Whether the write pin was asserted.</param>
internal readonly record struct Sm83SstCycle(ushort? Address, byte? Data, bool IsRead, bool IsWrite);

/// <summary>One vector's register file plus the flat-RAM bytes it touches.</summary>
/// <param name="A">The accumulator.</param>
/// <param name="B">The B register.</param>
/// <param name="C">The C register.</param>
/// <param name="D">The D register.</param>
/// <param name="E">The E register.</param>
/// <param name="F">The flags register.</param>
/// <param name="H">The H register.</param>
/// <param name="L">The L register.</param>
/// <param name="Pc">The program counter.</param>
/// <param name="Sp">The stack pointer.</param>
/// <param name="Ime">The interrupt-master-enable flag (0 or 1).</param>
/// <param name="Ei">Whether an EI-armed enable is still pending one instruction later (final-state only; absent means
/// false). Maps to the core's <c>m_interruptEnableCountdown != 0</c>.</param>
/// <param name="Ram">The (address, value) pairs this vector reads or writes.</param>
internal readonly record struct Sm83SstState(
    byte A, byte B, byte C, byte D, byte E, byte F, byte H, byte L,
    ushort Pc, ushort Sp, int Ime, bool Ei,
    IReadOnlyList<(ushort Address, byte Value)> Ram
);

/// <summary>One SingleStepTests/sm83 per-instruction test vector.</summary>
/// <param name="Name">The corpus's human-readable name for the case.</param>
/// <param name="Initial">The register/RAM state to seed before stepping.</param>
/// <param name="Final">The expected register/RAM state after exactly one <c>StepInstruction</c>.</param>
/// <param name="Cycles">The expected per-M-cycle bus-pin trace.</param>
internal readonly record struct Sm83SstVector(string Name, Sm83SstState Initial, Sm83SstState Final, IReadOnlyList<Sm83SstCycle> Cycles);

/// <summary>Deserializes one opcode family's JSON file (1000 vectors) from the SingleStepTests/sm83 corpus layout
/// (<c>v1/&lt;opcode&gt;.json</c>; CB-prefixed opcodes are named <c>"cb xx.json"</c>).</summary>
internal static class Sm83SstVectorFile {
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads and maps every vector in one opcode family's file.</summary>
    /// <param name="path">The JSON file's full path.</param>
    /// <returns>The parsed vectors, in file order.</returns>
    public static IReadOnlyList<Sm83SstVector> Load(string path) {
        using var stream = File.OpenRead(path: path);

        var dtos = JsonSerializer.Deserialize<List<VectorDto>>(utf8Json: stream, options: Options)
            ?? throw new InvalidDataException(message: $"'{path}' did not deserialize to a vector array.");
        var vectors = new List<Sm83SstVector>(capacity: dtos.Count);

        foreach (var dto in dtos) {
            vectors.Add(item: new Sm83SstVector(
                Name: (dto.Name ?? string.Empty),
                Initial: MapState(dto: (dto.Initial ?? throw new InvalidDataException(message: $"'{path}': a vector is missing 'initial'.")), ei: false),
                Final: MapState(dto: (dto.Final ?? throw new InvalidDataException(message: $"'{path}': a vector is missing 'final'.")), ei: ((dto.Final.Ei ?? 0) != 0)),
                Cycles: MapCycles(entries: dto.Cycles)
            ));
        }

        return vectors;
    }

    private static Sm83SstState MapState(StateDto dto, bool ei) {
        var ram = new (ushort Address, byte Value)[(dto.Ram?.Length ?? 0)];

        for (var index = 0; (index < ram.Length); ++index) {
            var pair = dto.Ram![index];

            ram[index] = ((ushort)pair[0], (byte)pair[1]);
        }

        return new Sm83SstState(
            A: (byte)dto.A, B: (byte)dto.B, C: (byte)dto.C, D: (byte)dto.D, E: (byte)dto.E, F: (byte)dto.F, H: (byte)dto.H, L: (byte)dto.L,
            Pc: (ushort)dto.Pc, Sp: (ushort)dto.Sp, Ime: dto.Ime, Ei: ei,
            Ram: ram
        );
    }
    private static IReadOnlyList<Sm83SstCycle> MapCycles(List<List<JsonElement>>? entries) {
        if (entries is null) {
            return [];
        }

        var cycles = new Sm83SstCycle[entries.Count];

        for (var index = 0; (index < entries.Count); ++index) {
            var entry = entries[index];
            var address = ((entry[0].ValueKind == JsonValueKind.Number) ? (ushort?)entry[0].GetUInt16() : null);
            var data = ((entry[1].ValueKind == JsonValueKind.Number) ? (byte?)entry[1].GetByte() : null);
            var flags = (entry[2].GetString() ?? string.Empty);

            cycles[index] = new Sm83SstCycle(
                Address: address,
                Data: data,
                IsRead: flags.Contains(value: 'r'),
                IsWrite: flags.Contains(value: 'w')
            );
        }

        return cycles;
    }

    private sealed class VectorDto {
        public string? Name { get; set; }
        public StateDto? Initial { get; set; }
        public StateDto? Final { get; set; }
        public List<List<JsonElement>>? Cycles { get; set; }
    }
    private sealed class StateDto {
        public int A { get; set; }
        public int B { get; set; }
        public int C { get; set; }
        public int D { get; set; }
        public int E { get; set; }
        public int F { get; set; }
        public int H { get; set; }
        public int L { get; set; }
        public int Pc { get; set; }
        public int Sp { get; set; }
        public int Ime { get; set; }
        public int? Ie { get; set; }
        public int? Ei { get; set; }
        public int[][]? Ram { get; set; }
    }
}
