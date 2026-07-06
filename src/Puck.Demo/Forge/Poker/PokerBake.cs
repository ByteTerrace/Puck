using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Demo.Forge.Cards;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// Bakes Poker's SDF-authored art on the one-shot GPU host and installs it through the <see cref="PokerTables"/>
/// seams: the title emblem (a five-card fan dressed with the card layer's shared suit-symbol vocabulary beside two
/// chip stacks over felt), the felt table the play screen composes onto, and the two-frame table cursor. Each bake
/// is independent and best-effort by design: any failure or blown budget narrates and leaves that piece's
/// hand-authored fallback in place, never failing the forge — and the verify battery runs OUTSIDE the GPU host so
/// a fallback can never mask a game failure.
/// </summary>
internal static class PokerBake {
    // The shares of the composed 256-tile bank and the 8+8 palette slots (the card tiles + font take 81 tiles and
    // background slot 0 / object slot 0; the guards keep the three bakes inside the remainder).
    private const int TitleTileBudget = 120;
    private const int TitlePaletteBudget = 5;
    private const int FeltTileBudget = 40;
    private const int FeltPaletteBudget = 2;
    private const int CursorTileBudget = 16;
    /// <summary>The per-frame cursor OAM entry ceiling (the game reserves this many shadow slots; well under the
    /// 40-slot table, and the pointer's footprint keeps scanline stacking shallow).</summary>
    public const int CursorEntryBudget = 10;

    /// <summary>Bakes and installs the title, felt, and cursor. Never throws.</summary>
    /// <param name="device">The live (or one-shot) GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <returns>Whether every piece installed baked (false = at least one hand-authored fallback ships).</returns>
    public static bool TryInstall(IGpuDeviceContext device, IGpuComputeServices gpu) {
        PokerTables.SetTitleArt(art: CardArtBake.BakeBackgroundArt(device: device, gpu: gpu, name: "poker-title", paletteBudget: TitlePaletteBudget, scene: BuildTitleScene(), tileBudget: TitleTileBudget));
        PokerTables.SetFeltArt(art: CardArtBake.BakeBackgroundArt(device: device, gpu: gpu, name: "poker-felt", paletteBudget: FeltPaletteBudget, scene: CardArtBake.BuildFeltScene(), tileBudget: FeltTileBudget));
        PokerTables.SetCursorArt(art: CardArtBake.BakeCursorSprites(device: device, entryBudget: CursorEntryBudget, gpu: gpu, tileBudget: CursorTileBudget));

        return ((PokerTables.TitleArt is not null) && (PokerTables.FeltArt is not null) && (PokerTables.CursorArt is not null));
    }

    // The title emblem: a five-card fan (spade, heart, diamond, club faces around a red-backed pivot) over the
    // felt, with two chip stacks on the lower rail. World frame: x ∈ [-10, 10], y ∈ [-9, 9], one unit = one tile.
    private static SdfProgram BuildTitleScene() {
        var builder = new SdfProgramBuilder();
        var felt = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.10f, 0.38f, 0.22f), Emissive: 1.2f));
        var rail = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.24f, 0.14f, 0.08f), Emissive: 1.1f));
        var face = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.93f, 0.94f, 0.92f), Emissive: 1.3f));
        var ink = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.08f, 0.08f, 0.11f), Emissive: 1.0f));
        var red = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.80f, 0.14f, 0.18f), Emissive: 1.2f));
        var chipBlue = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.20f, 0.32f, 0.72f), Emissive: 1.2f));
        var chipGold = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.78f, 0.30f), Emissive: 1.3f));

        // The table: felt inside a rail, like the play screen's backdrop.
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0f, -3f)).Box(halfExtents: new Vector3(30f, 20f, 0.5f), round: 0f, material: rail);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0f, -2f)).Box(halfExtents: new Vector3(9f, 8f, 0.4f), round: 0.2f, material: felt);

        // The fan: four suit faces splayed around centre, each dressed from the shared vocabulary.
        CardArtBake.AddCardShape(builder: builder, centre: new Vector3(-5.4f, 0.4f, -0.9f), faceMaterial: face, halfExtents: new Vector2(1.7f, 2.4f), tiltRadians: (30f * (MathF.PI / 180f)));
        CardArtBake.AddSuitSymbol(builder: builder, centre: new Vector3(-5.4f, 0.4f, -0.3f), material: ink, scale: 1.0f, suit: 0);
        CardArtBake.AddCardShape(builder: builder, centre: new Vector3(-1.8f, 1.2f, -0.6f), faceMaterial: face, halfExtents: new Vector2(1.8f, 2.5f), tiltRadians: (12f * (MathF.PI / 180f)));
        CardArtBake.AddSuitSymbol(builder: builder, centre: new Vector3(-1.8f, 1.2f, 0f), material: red, scale: 1.15f, suit: 1);
        CardArtBake.AddCardShape(builder: builder, centre: new Vector3(1.8f, 1.2f, -0.3f), faceMaterial: face, halfExtents: new Vector2(1.8f, 2.5f), tiltRadians: (-12f * (MathF.PI / 180f)));
        CardArtBake.AddSuitSymbol(builder: builder, centre: new Vector3(1.8f, 1.2f, 0.3f), material: red, scale: 1.15f, suit: 2);
        CardArtBake.AddCardShape(builder: builder, centre: new Vector3(5.4f, 0.4f, 0f), faceMaterial: face, halfExtents: new Vector2(1.7f, 2.4f), tiltRadians: (-30f * (MathF.PI / 180f)));
        CardArtBake.AddSuitSymbol(builder: builder, centre: new Vector3(5.4f, 0.4f, 0.6f), material: ink, scale: 1.0f, suit: 3);

        // Two chip stacks on the lower felt: thin rounded discs, red/blue with a gold cap.
        for (var chip = 0; (chip < 3); chip++) {
            var material = ((chip == 2) ? chipGold : red);

            _ = builder.ResetPoint().Translate(offset: new Vector3(-6.6f, (-6.2f + (chip * 0.55f)), 0.4f)).Box(halfExtents: new Vector3(0.9f, 0.22f, 0.9f), round: 0.18f, material: material);
        }

        for (var chip = 0; (chip < 3); chip++) {
            var material = ((chip == 2) ? chipGold : chipBlue);

            _ = builder.ResetPoint().Translate(offset: new Vector3(6.6f, (-6.2f + (chip * 0.55f)), 0.4f)).Box(halfExtents: new Vector3(0.9f, 0.22f, 0.9f), round: 0.18f, material: material);
        }

        return builder.Build();
    }
}
