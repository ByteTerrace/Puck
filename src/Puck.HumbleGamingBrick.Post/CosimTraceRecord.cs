namespace Puck.HumbleGamingBrick.Post;

/// <summary>The conceptual-event kinds the co-simulation trace format carries.</summary>
internal enum CosimEventKind : byte {
    Cpu = 0,
    PpuMode = 1,
    PpuPixel = 2,
    Pcm = 3,
}

/// <summary>
/// One fixed 32-byte little-endian record of the co-simulation trace format shared with SameBoy's <c>sb-trace events</c>
/// mode (<c>D:\Source\ByteTerrace\Temp\SameBoy\trace_main.c</c>): 8 bytes cycle (the master T-cycle, 4&#8201;MHz, since
/// reset) + 1 byte <see cref="CosimEventKind"/> + 7 reserved zero bytes + 16 bytes of kind-specific payload. KEEP IN
/// SYNC with trace_main.c's <c>write_event</c>/<c>on_execution</c>/<c>run_events</c>.
/// <list type="bullet">
/// <item><description><see cref="CosimEventKind.Cpu"/>: u16 pc, u8 a, u8 f, u8 b, u8 c, u8 d, u8 e, u8 h, u8 l, u16 sp,
/// 4 bytes padding.</description></item>
/// <item><description><see cref="CosimEventKind.PpuMode"/>: u8 ly, u8 mode, 14 bytes padding.</description></item>
/// <item><description><see cref="CosimEventKind.PpuPixel"/>: u8 ly, u8 x, u32 color (<c>0x00RRGGBB</c>), 10 bytes
/// padding.</description></item>
/// <item><description><see cref="CosimEventKind.Pcm"/>: u8 pcm12, u8 pcm34, 14 bytes padding.</description></item>
/// </list>
/// SameBoy samples STAT/LY/PCM once per <c>GB_run()</c> call rather than once per T-cycle (see trace_main.c's
/// <c>run_events</c> comment) — a call can span several T-cycles, so a <see cref="CosimEventKind.PpuMode"/> or
/// <see cref="CosimEventKind.Pcm"/> record's <see cref="Cycle"/> can trail the true internal edge by a handful of
/// T-cycles, and a <see cref="CosimEventKind.PpuPixel"/> record's <see cref="Cycle"/> is the whole scanline's
/// mode-3-exit cycle shared by all 160 columns, not a true per-pixel timestamp. Only <see cref="CosimEventKind.Cpu"/>
/// is pushed on both sides at the true instruction boundary, so only it compares <see cref="Cycle"/> exactly;
/// <see cref="CycleIsExact"/> reports that so a comparison can skip the cycle column for the other three kinds while
/// still comparing their content fields exactly.
/// </summary>
internal readonly struct CosimEvent {
    public const int ByteSize = 32;
    private const int PayloadSize = 16;
    private const int ReservedSize = 7;

    public required ulong Cycle { get; init; }
    public required CosimEventKind Kind { get; init; }

    public ushort Pc { get; init; }
    public byte A { get; init; }
    public byte F { get; init; }
    public byte B { get; init; }
    public byte C { get; init; }
    public byte D { get; init; }
    public byte E { get; init; }
    public byte H { get; init; }
    public byte L { get; init; }
    public ushort Sp { get; init; }

    public byte Ly { get; init; }
    public int Mode { get; init; }

    public int X { get; init; }
    public uint Color { get; init; }

    public byte Pcm12 { get; init; }
    public byte Pcm34 { get; init; }

    /// <summary>Gets a value indicating whether <see cref="Cycle"/> is an exact instruction-boundary stamp rather than
    /// a SameBoy-side <c>GB_run()</c>-call-boundary sample (see the type remarks).</summary>
    public bool CycleIsExact => (Kind == CosimEventKind.Cpu);

    /// <summary>Reads one 32-byte record, or <see langword="null"/> at a clean end of stream.</summary>
    public static CosimEvent? TryReadFrom(BinaryReader reader) {
        var cycleBytes = reader.ReadBytes(count: 8);

        if (cycleBytes.Length == 0) {
            return null;
        }

        if (cycleBytes.Length != 8) {
            throw new EndOfStreamException(message: "Truncated cosim trace record (cycle field).");
        }

        var cycle = BitConverter.ToUInt64(value: cycleBytes);
        var kind = ((CosimEventKind)reader.ReadByte());

        reader.ReadBytes(count: ReservedSize);

        var payload = reader.ReadBytes(count: PayloadSize);

        if (payload.Length != PayloadSize) {
            throw new EndOfStreamException(message: "Truncated cosim trace record (payload field).");
        }

        return kind switch {
            CosimEventKind.Cpu => new CosimEvent {
                A = payload[2],
                B = payload[4],
                C = payload[5],
                Cycle = cycle,
                D = payload[6],
                E = payload[7],
                F = payload[3],
                H = payload[8],
                Kind = kind,
                L = payload[9],
                Pc = BitConverter.ToUInt16(value: payload.AsSpan(start: 0, length: 2)),
                Sp = BitConverter.ToUInt16(value: payload.AsSpan(start: 10, length: 2)),
            },
            CosimEventKind.PpuMode => new CosimEvent {
                Cycle = cycle,
                Kind = kind,
                Ly = payload[0],
                Mode = payload[1],
            },
            CosimEventKind.PpuPixel => new CosimEvent {
                Color = BitConverter.ToUInt32(value: payload.AsSpan(start: 2, length: 4)),
                Cycle = cycle,
                Kind = kind,
                Ly = payload[0],
                X = payload[1],
            },
            CosimEventKind.Pcm => new CosimEvent {
                Cycle = cycle,
                Kind = kind,
                Pcm12 = payload[0],
                Pcm34 = payload[1],
            },
            _ => throw new InvalidDataException(message: $"Unknown cosim event kind {(byte)kind}."),
        };
    }
    /// <summary>Writes this record as the fixed 32-byte layout <see cref="TryReadFrom"/> reads back.</summary>
    public void WriteTo(BinaryWriter writer) {
        Span<byte> payload = stackalloc byte[PayloadSize];

        payload.Clear();

        switch (Kind) {
            case CosimEventKind.Cpu:
                BitConverter.TryWriteBytes(
                    destination: payload[..2],
                    value: Pc
                );
                payload[2] = A;
                payload[3] = F;
                payload[4] = B;
                payload[5] = C;
                payload[6] = D;
                payload[7] = E;
                payload[8] = H;
                payload[9] = L;
                BitConverter.TryWriteBytes(
                    destination: payload[10..12],
                    value: Sp
                );

                break;
            case CosimEventKind.PpuMode:
                payload[0] = Ly;
                payload[1] = ((byte)Mode);

                break;
            case CosimEventKind.PpuPixel:
                payload[0] = Ly;
                payload[1] = ((byte)X);
                BitConverter.TryWriteBytes(
                    destination: payload[2..6],
                    value: Color
                );

                break;
            case CosimEventKind.Pcm:
                payload[0] = Pcm12;
                payload[1] = Pcm34;

                break;
        }

        writer.Write(value: Cycle);
        writer.Write(value: ((byte)Kind));
        writer.Write(buffer: stackalloc byte[ReservedSize]);
        writer.Write(buffer: payload);
    }
    /// <summary>Compares the kind-specific payload fields for equality (excluding <see cref="Cycle"/>, which the
    /// caller compares separately when <see cref="CycleIsExact"/> holds for both records).</summary>
    public bool ContentEquals(in CosimEvent other) =>
        ((Kind == other.Kind) && (Kind switch {
            CosimEventKind.Cpu =>
                (Pc == other.Pc) &&
                (A == other.A) &&
                (F == other.F) &&
                (B == other.B) &&
                (C == other.C) &&
                (D == other.D) &&
                (E == other.E) &&
                (H == other.H) &&
                (L == other.L) &&
                (Sp == other.Sp),
            CosimEventKind.PpuMode => ((Ly == other.Ly) && (Mode == other.Mode)),
            CosimEventKind.PpuPixel => ((Ly == other.Ly) && (X == other.X) && (Color == other.Color)),
            CosimEventKind.Pcm => ((Pcm12 == other.Pcm12) && (Pcm34 == other.Pcm34)),
            _ => false,
        }));
    /// <summary>Renders a one-line human-readable form for divergence reports.</summary>
    public string Describe() =>
        Kind switch {
            CosimEventKind.Cpu => $"cyc={Cycle,10} CPU   pc={Pc:X4} a={A:X2} f={F:X2} b={B:X2} c={C:X2} d={D:X2} e={E:X2} h={H:X2} l={L:X2} sp={Sp:X4}",
            CosimEventKind.PpuMode => $"cyc={Cycle,10} MODE  ly={Ly,3} mode={Mode}",
            CosimEventKind.PpuPixel => $"cyc~{Cycle,10} PIXEL ly={Ly,3} x={X,3} color=0x{Color:X6}",
            CosimEventKind.Pcm => $"cyc={Cycle,10} PCM   pcm12={Pcm12:X2} pcm34={Pcm34:X2}",
            _ => $"cyc={Cycle} kind={Kind}",
        };
}
