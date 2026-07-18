using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Demo.Forge.Cards;
using Puck.SdfVm;

namespace Puck.Demo.Forge;

/// <summary>
/// Bakes Solitaire's SDF-authored art on the one-shot GPU host and installs it through the
/// <see cref="SolitaireTables"/> seams: the title emblem (a fan of three cards — spade, heart, diamond faces built
/// from the card layer's shared suit-symbol vocabulary — with a lattice-backed card tumbling behind them over
/// felt), the felt table the play screen composes onto, and the two-frame table cursor. Each bake is independent
/// and best-effort by design: any failure or blown budget narrates and leaves that piece's hand-authored fallback
/// in place, never failing the forge — and the verify battery runs OUTSIDE the GPU host so a fallback can never
/// mask a game failure.
/// </summary>
internal static class SolitaireBake {
    // The shares of the composed 256-tile bank and the 8+8 palette slots (the card tiles + font take 81 tiles and
    // background slot 0 / object slot 0; the guards keep the three bakes inside the remainder).
    private const int TitleTileBudget = 110;
    private const int CursorTileBudget = 16;
    private const int FeltPaletteBudget = 2;
    private const int FeltTileBudget = 40;
    private const int TitlePaletteBudget = 5;
    /// <summary>The per-frame cursor OAM entry ceiling (the game reserves this many shadow slots; well under the
    /// 40-slot table, and the pointer's footprint keeps scanline stacking shallow).</summary>
    public const int CursorEntryBudget = 10;

    /// <summary>Bakes and installs the title, felt, and cursor. Never throws.</summary>
    /// <param name="device">The live (or one-shot) GPU device.</param>
    /// <param name="gpu">The compute services.</param>
    /// <returns>Whether every piece installed baked (false = at least one hand-authored fallback ships).</returns>
    public static bool TryInstall(IGpuDeviceContext device, IGpuComputeServices gpu) {
        SolitaireTables.SetTitleArt(art: CardArtBake.BakeBackgroundArt(device: device, gpu: gpu, name: "solitaire-title", paletteBudget: TitlePaletteBudget, scene: BuildTitleScene(), tileBudget: TitleTileBudget));
        SolitaireTables.SetFeltArt(art: CardArtBake.BakeBackgroundArt(device: device, gpu: gpu, name: "solitaire-felt", paletteBudget: FeltPaletteBudget, scene: CardArtBake.BuildFeltScene(), tileBudget: FeltTileBudget));
        SolitaireTables.SetCursorArt(art: CardArtBake.BakeCursorSprites(device: device, entryBudget: CursorEntryBudget, gpu: gpu, tileBudget: CursorTileBudget));

        return ((SolitaireTables.TitleArt is not null) && (SolitaireTables.FeltArt is not null) && (SolitaireTables.CursorArt is not null));
    }

    // The title emblem: three fanned card faces over the felt, each dressed with a shared suit symbol, and a
    // lattice-red card back tumbling behind the fan. World frame: x ∈ [-10, 10], y ∈ [-9, 9], one unit = one tile.
    private static SdfProgram BuildTitleScene() {
        var builder = new SdfProgramBuilder();
        var felt = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.10f, y: 0.38f, z: 0.22f), Emissive: 1.2f));
        var rail = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.24f, y: 0.14f, z: 0.08f), Emissive: 1.1f));
        var face = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.93f, y: 0.94f, z: 0.92f), Emissive: 1.3f));
        var ink = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.08f, y: 0.08f, z: 0.11f), Emissive: 1.0f));
        var red = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.80f, y: 0.14f, z: 0.18f), Emissive: 1.2f));
        var backRed = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.62f, y: 0.12f, z: 0.16f), Emissive: 1.1f));

        // The table: felt inside a rail, like the play screen's backdrop.
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0f, z: -3f)).Box(halfExtents: new Vector3(x: 30f, y: 20f, z: 0.5f), round: 0f, material: rail);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0f, z: -2f)).Box(halfExtents: new Vector3(x: 9f, y: 8f, z: 0.4f), round: 0.2f, material: felt);

        // The tumbling back, behind the fan.
        CardArtBake.AddCardShape(builder: builder, centre: new Vector3(x: 4.6f, y: 3.4f, z: -1f), faceMaterial: backRed, halfExtents: new Vector2(x: 1.7f, y: 2.3f), tiltRadians: (28f * (MathF.PI / 180f)));

        // The fan: spade, heart, diamond faces, each with its symbol from the shared vocabulary.
        CardArtBake.AddCardShape(builder: builder, centre: new Vector3(x: -4.2f, y: 0.6f, z: -0.5f), faceMaterial: face, halfExtents: new Vector2(x: 1.8f, y: 2.5f), tiltRadians: (18f * (MathF.PI / 180f)));
        CardArtBake.AddSuitSymbol(builder: builder, centre: new Vector3(x: -4.2f, y: 0.6f, z: 0.1f), material: ink, scale: 1.1f, suit: 0);
        CardArtBake.AddCardShape(builder: builder, centre: new Vector3(x: 0f, y: 1.1f, z: 0f), faceMaterial: face, halfExtents: new Vector2(x: 1.9f, y: 2.6f), tiltRadians: 0f);
        CardArtBake.AddSuitSymbol(builder: builder, centre: new Vector3(x: 0f, y: 1.1f, z: 0.55f), material: red, scale: 1.25f, suit: 1);
        CardArtBake.AddCardShape(builder: builder, centre: new Vector3(x: 4.2f, y: 0.6f, z: 0.3f), faceMaterial: face, halfExtents: new Vector2(x: 1.8f, y: 2.5f), tiltRadians: (-18f * (MathF.PI / 180f)));
        CardArtBake.AddSuitSymbol(builder: builder, centre: new Vector3(x: 4.2f, y: 0.6f, z: 0.8f), material: red, scale: 1.1f, suit: 2);

        // A small club punctuating the lower felt.
        CardArtBake.AddSuitSymbol(builder: builder, centre: new Vector3(x: -6.5f, y: -5.6f, z: 0f), material: ink, scale: 0.8f, suit: 3);

        return builder.Build();
    }
}
