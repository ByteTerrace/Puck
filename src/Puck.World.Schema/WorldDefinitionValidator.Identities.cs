using Puck.Assets.Documents;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Each seed becomes an owned-world document on disk under WorldDocumentName's id→file-name mapping, so an id is a
    // document name and is held to the one rule for when two are one (DocumentName): NTFS and default APFS resolve a
    // name case-insensitively, so 'Amber' and 'amber' address one file. It is the same rule Server.WorldOwnedWorlds
    // holds over the directory itself, refused in the same words.
    private static void ValidateIdentitySeeds(IReadOnlyList<WorldIdentitySeed> identities, List<string> errors) {
        var ids = new Dictionary<string, (string Id, string Path)>(comparer: DocumentName.Comparer);
        var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        for (var index = 0; (index < identities.Count); index++) {
            var profile = identities[index];
            var path = $"seatDefaults.identities[{index}]";

            if (profile is null) {
                errors.Add(item: $"{path} is required.");
                continue;
            }

            if (ids.TryGetValue(
                key: profile.Id,
                value: out var held
            )) {
                errors.Add(item: DocumentName.Collision(
                    heldFile: $"{held.Path}.id",
                    heldName: held.Id,
                    otherFile: $"{path}.id",
                    otherName: profile.Id
                ));
            } else {
                ids.Add(
                    key: profile.Id,
                    value: (profile.Id, path)
                );
            }

            if (
                string.IsNullOrWhiteSpace(value: profile.Name) ||
                !names.Add(item: profile.Name)
            ) {
                errors.Add(item: $"{path}.name is required and unique ignoring case.");
            }

            if (!IsHexColor(value: profile.Color)) {
                errors.Add(item: $"{path}.color must be #RRGGBB.");
            }
        }
    }
}
