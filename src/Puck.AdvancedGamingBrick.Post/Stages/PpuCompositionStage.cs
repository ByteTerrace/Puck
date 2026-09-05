namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Checks layer ordering, transparency, window masks and effects against an independently sorted pixel model.</summary>
internal sealed class PpuCompositionStage : IPostStage<PostContext> {
    private static readonly ushort[] Colors = [0x001F, 0x03E0, 0x7C00, 0x7FFF, 0x4210, 0x2108];
    private static readonly ushort[] Masks = [0x3F, 0x3E, 0x35, 0x2F, 0x1F, 0x22, 0x30, 0x20];

    /// <inheritdoc/>
    public string Name => "ppu-composition";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        for (var priorities = 0; (priorities < 256); ++priorities) {
            for (var objectPriority = 0; (objectPriority < 4); ++objectPriority) {
                var scheduler = new AgbScheduler();
                var ppu = BuildPpu(scheduler: scheduler, priorities: priorities, objectPriority: objectPriority);

                for (var variant = 0; (variant < Masks.Length); ++variant) {
                    var effect = variant & 3;
                    var semiTransparent = ((variant & 1) != 0);
                    ppu.WriteRegister(offset: 0x48u, value: Masks[variant]);
                    ppu.WriteRegister(offset: 0x50u, value: ((ushort)(0x2A15 | (effect << 6))));
                    ppu.WriteVideo(address: 0x07000000u, width: 2,
                        value: ((uint)(0x4000 | variant | (semiTransparent ? 0x400 : 0))));
                    scheduler.Advance(cycles: 1232);

                    for (var x = 0; (x < 16); ++x) {
                        var expected = ExpectedPixel(priorities: priorities, objectPriority: objectPriority,
                            visible: (x | 0x10) & Masks[variant], effectsAllowed: ((Masks[variant] & 0x20) != 0),
                            effect: effect, semiTransparent: semiTransparent);
                        var actual = ppu.Framebuffer[((variant * 240) + x)];

                        if (actual != expected) {
                            return PostStageOutcome.Fail(detail:
                                $"priorities={priorities:X2}, OBJ={objectPriority}, variant={variant}, x={x}: {actual:X8} != {expected:X8}");
                        }
                    }
                }
            }
        }

        return PostStageOutcome.Pass(detail: "131072 pixels: all BG/OBJ priority combinations, BG transparency subsets, window masks, alpha/brightness and semi-transparent OBJ");
    }

    private static AgbPpu BuildPpu(AgbScheduler scheduler, int priorities, int objectPriority) {
        var ppu = new AgbPpu(scheduler: scheduler, interrupts: new AgbInterruptController());
        ppu.WriteRegister(offset: 0u, value: 0x3F00); // Mode 0, all layers, WIN0.
        ppu.WriteRegister(offset: 0x40u, value: 240);
        ppu.WriteRegister(offset: 0x44u, value: 160);
        ppu.WriteRegister(offset: 0x52u, value: 0x0808);
        ppu.WriteRegister(offset: 0x54u, value: 16);
        ppu.WriteVideo(address: 0x05000000u, value: Colors[5], width: 2);
        ppu.WriteVideo(address: 0x05000202u, value: Colors[4], width: 2);

        for (var background = 0; (background < 4); ++background) {
            ppu.WriteRegister(offset: ((uint)(8 + (background * 2))),
                value: ((ushort)(((28 + background) << 8) | (background << 2) | ((priorities >> (2 * background)) & 3))));
            ppu.WriteVideo(address: (0x05000002u + ((uint)background * 32u)), value: Colors[background], width: 2);

            for (var tile = 0; (tile < 2); ++tile) {
                ppu.WriteVideo(address: (0x0600E000u + ((uint)background * 0x800u) + ((uint)tile * 2u)),
                    value: ((uint)((background << 12) | tile)), width: 2);

                for (var y = 0; (y < 8); ++y) {
                    for (var x = 0; (x < 8); x += 4) {
                        var pixels = 0u;

                        for (var lane = 0; (lane < 4); ++lane) {
                            pixels |= ((uint)((((tile * 8) + x + lane) >> background) & 1)) << (lane * 4);
                        }

                        ppu.WriteVideo(address: (0x06000000u + ((uint)background * 0x4000u)
                            + ((uint)((tile * 32) + (y * 4) + (x / 2)))), value: pixels, width: 2);
                    }
                }
            }
        }

        for (var index = 1; (index < 128); ++index) {
            ppu.WriteVideo(address: (0x07000000u + ((uint)index * 8u)), value: 0x0200, width: 2);
        }

        // A 16x8 OBJ with opaque pixels, redrawn at each tested scanline's Y coordinate.
        ppu.WriteVideo(address: 0x07000004u, value: ((uint)objectPriority << 10), width: 2);

        for (var offset = 0u; (offset < 64u); offset += 2u) {
            ppu.WriteVideo(address: (0x06010000u + offset), value: 0x1111, width: 2);
        }

        return ppu;
    }

    private static uint ExpectedPixel(int priorities, int objectPriority, int visible, bool effectsAllowed, int effect, bool semiTransparent) {
        // Full sorting is intentionally independent of the renderer's scanline ordering and two-layer insertion.
        var layers = Enumerable.Range(start: 0, count: 6)
            .Where(predicate: id => (id == 5) || ((visible & (1 << id)) != 0))
            .OrderBy(keySelector: id => ((id == 5) ? 40 : ((id == 4) ? (objectPriority * 8) : ((((priorities >> (id * 2)) & 3) * 8) + id + 1))))
            .ToArray();
        var top = layers[0];
        var second = ((layers.Length > 1) ? layers[1] : 5);
        var alpha = effectsAllowed && ((0x2A & (1 << second)) != 0)
            && (((top == 4) && semiTransparent) || ((effect == 1) && ((0x15 & (1 << top)) != 0)));
        var result = 0xFF000000u;

        for (var channel = 0; (channel < 3); ++channel) {
            var value = (Colors[top] >> (channel * 5)) & 31;

            if (alpha) {
                value = (value + ((Colors[second] >> (channel * 5)) & 31)) / 2;
            } else if (effectsAllowed && ((0x15 & (1 << top)) != 0)) {
                value = effect switch { 2 => 31, 3 => 0, _ => value };
            }

            result |= ((uint)((value * 8) + (value / 4))) << (channel * 8);
        }

        return result;
    }
}
