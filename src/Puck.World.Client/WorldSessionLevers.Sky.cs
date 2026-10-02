using System.Globalization;
using Puck.Abstractions.Presentation;

namespace Puck.World.Client;

public static partial class WorldSessionLevers {
    /// <summary>The live sky quality override (<c>world.sky.quality</c>), using the existing quality tier ordinals.</summary>
    public const string SkyQuality = "sky.quality";
    /// <summary>The wire value that clears the sky quality override and resumes each world's authored quality.</summary>
    public const double SkyQualityAuto = -1d;

    private static void RegisterSky(WorldSessionLeverSink sink, WorldRenderSettings settings) {
        sink.Register(name: SkyQuality, setter: lever => {
            if (lever.A == SkyQualityAuto) {
                settings.SkyQuality = null;
                return;
            }
            foreach (var tier in QualityTiers.All) {
                if (lever.A == ((double)tier)) {
                    settings.SkyQuality = tier;
                    return;
                }
            }
            Console.Error.WriteLine(value: string.Create(CultureInfo.InvariantCulture,
                $"[world.sky.quality: invalid tier '{lever.A:R}' — expected low|medium|high|auto; lever dropped]"));
        });
    }
}
