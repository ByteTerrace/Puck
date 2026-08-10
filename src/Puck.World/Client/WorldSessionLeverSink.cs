using Puck.Launcher;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// Writes an accepted <see cref="WorldSessionLever"/> onto the live presentation service it names — the client half of
/// the lever path, and the only place these knobs are written. The console modules that reference
/// <see cref="WorldRenderSettings"/>, <see cref="PresentPacingControl"/>, and the audio director read their echoes
/// only; the write travels
/// <c>verb → IServerLink.SubmitSessionLever → WorldServer.ApplySessionLever (the Mutate check) → IClientSink → here</c>.
/// </summary>
/// <remarks>
/// <para>Reached only past the server's grant check, so this type never decides anything — it dispatches. That split is
/// deliberate: one boundary owns the authority question, and adding a knob here can never accidentally add an
/// unchecked write, because the only way in is through the checked path.</para>
/// <para>Every knob here is presentation state (render settings, present pacing, audio mix gain). Nothing under
/// <c>Server/</c> reads any of it, which is what makes the lever's <see cref="double"/> value lanes safe — see
/// <see cref="WorldSessionLever"/>'s own remarks for why a simulation-read knob may not become a lever.</para>
/// </remarks>
/// <param name="settings">The live render-lever settings the frame source reads.</param>
/// <param name="pacing">The live present-rate control the window pump reads.</param>
/// <param name="audio">The audio director owning the master-volume session lever.</param>
internal sealed class WorldSessionLeverSink(WorldRenderSettings settings, PresentPacingControl pacing, WorldAudioDirector audio) {
    /// <summary>Applies one accepted lever.</summary>
    /// <param name="lever">The lever to write.</param>
    public void Apply(WorldSessionLever lever) {
        switch (lever.Kind) {
            case WorldLeverKind.MasterVolume:
                audio.SetMasterVolume(value: (float)lever.A);

                break;
            case WorldLeverKind.Shadows:
                settings.ShadowReach = (float)lever.A;
                settings.ShadowCrowdRadius = (float)lever.B;

                break;
            case WorldLeverKind.AmbientOcclusion:
                settings.AmbientOcclusion = (lever.A != 0.0);

                break;
            case WorldLeverKind.AmbientOcclusionQuality:
                settings.AmbientOcclusionQuality = (AmbientOcclusionMode)(int)lever.A;

                break;
            case WorldLeverKind.FarBound:
                settings.FarBound = (lever.A != 0.0);

                break;
            case WorldLeverKind.ShadowFarExit:
                settings.ShadowFarExit = (lever.A != 0.0);

                break;
            case WorldLeverKind.ShadowAccumulation:
                settings.ShadowAccumulation = (lever.A != 0.0);

                break;
            case WorldLeverKind.ShadowMask:
                settings.ShadowMask = (ShadowMaskMode)(int)lever.A;

                break;
            case WorldLeverKind.ShadowMarch:
                settings.ShadowMarch = (ShadowMarchMode)(int)lever.A;

                break;
            case WorldLeverKind.RenderScale:
                settings.RenderScale = (float)lever.A;

                break;
            case WorldLeverKind.UpscaleSharpness:
                settings.UpscaleSharpness = (float)lever.A;

                break;
            default:
                pacing.SetTargetHertz(targetHertz: lever.A);

                break;
        }
    }
}
