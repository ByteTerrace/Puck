using System.Text.Json;

namespace Puck.World.Server;

/// <summary>One configured participant: an installed <see cref="Protocol.WorldParticipantType"/> acting in the world as
/// one principal over one body. The host approves it by listing it in the extension configuration; the world's grants
/// still decide every read and action it attempts.</summary>
/// <param name="Name">Deployment-local participant name, echoed by <c>world.extensions</c>.</param>
/// <param name="Type">Installed <see cref="Protocol.WorldParticipantType"/> key.</param>
/// <param name="Principal">Canonical acting principal, such as <c>addon:guide</c>.</param>
/// <param name="Body">The 0-based body the participant controls; inside the world's population capacity.</param>
/// <param name="Settings">Private participant settings, validated by the selected type before any work starts.</param>
public sealed record WorldExtensionParticipantSettings(string Name, string Type, string Principal, int Body, JsonElement Settings);
