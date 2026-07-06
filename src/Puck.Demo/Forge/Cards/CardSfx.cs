namespace Puck.Demo.Forge.Cards;

/// <summary>
/// The card games' shared sound-effect ids, triggered through the framework's <see cref="Framework.ISoundDriver"/>
/// seam (<c>Sound.EmitEffect</c>) at every deal/flip/place/win moment. Each id aliases a stream in the framework's
/// shared <see cref="Framework.SoundTables"/> catalog — the card vocabulary on the left, the catalog's voice on the
/// right — so every card game rings the same sounds without touching the driver.
/// </summary>
internal static class CardSfx {
    /// <summary>The full-deck shuffle (a new deal, or the stock recycling).</summary>
    public const byte Shuffle = Framework.SoundTables.EffectShuffle;
    /// <summary>A single card turning over (a stock draw, a tableau flip).</summary>
    public const byte Flip = Framework.SoundTables.EffectFlip;
    /// <summary>A card (or run) landing on a legal target — the catalog's card-slide.</summary>
    public const byte Place = Framework.SoundTables.EffectDeal;
    /// <summary>An illegal action (a rejected drop, an empty pickup, an empty undo).</summary>
    public const byte Error = Framework.SoundTables.EffectThud;
    /// <summary>The win fanfare.</summary>
    public const byte Win = Framework.SoundTables.EffectWin;
    /// <summary>A move taken back — the catalog's rising sweep, played back the other way in spirit.</summary>
    public const byte Undo = Framework.SoundTables.EffectSweep;
}
