import { Badge, Group, Stack, Text } from "@mantine/core";
import { useStudioDocument } from "../../context/StudioContext";
import type { JsonPath } from "../../document/jsonPath";

/**
 * Renders `document.diagnostics` (engine path + message, one per row) and `document.deferred`
 * (informational — a document feature the engine parsed but does not yet evaluate) as two
 * separate lists. Each diagnostic's "reveal" selects the Sections tab and the root section its
 * `path` starts under — see `WorldStudio.tsx`'s own `onRevealSection`; a diagnostic with no
 * leading path segment (a whole-document refusal) has nothing to reveal and renders without the
 * affordance.
 */
export function WorldStudioAlerts({ onReveal }: { readonly onReveal: (path: JsonPath) => void }) {
  const document = useStudioDocument();

  if (document.diagnostics.length === 0 && document.deferred.length === 0) {
    return (
      <div className="studio-status" role="status" aria-live="polite">
        <Text size="sm" c="var(--ink-soft)">
          {document.validation === "clean" ? "Document validated · No diagnostics." : "Document loaded."}
        </Text>
      </div>
    );
  }

  return (
    <div className="studio-status" role="status" aria-live="polite">
      <Stack gap={6}>
        {document.diagnostics.map((diagnostic, index) => {
          const firstSegment = diagnostic.path.split(/[.[]/)[0];
          return (
            <Group key={index} gap="xs" wrap="nowrap" align="flex-start">
              <Badge color="red" variant="light" size="xs" style={{ flexShrink: 0 }}>refused</Badge>
              <Text size="sm" c="var(--accent)" style={{ flex: 1 }}>
                {diagnostic.path ? <Text component="span" ff="monospace" size="xs" mr={6}>{diagnostic.path}</Text> : null}
                {diagnostic.message}
              </Text>
              {firstSegment && (
                <Text
                  component="button"
                  size="xs"
                  c="var(--accent-2)"
                  style={{ background: "none", border: "none", cursor: "pointer", padding: 0, flexShrink: 0 }}
                  onClick={() => onReveal([firstSegment])}
                >
                  reveal
                </Text>
              )}
            </Group>
          );
        })}
        {document.deferred.map((name, index) => (
          <Group key={`deferred-${index}`} gap="xs" wrap="nowrap">
            <Badge color="yellow" variant="light" size="xs">deferred</Badge>
            <Text size="xs" c="var(--ink-soft)" ff="monospace">{name}</Text>
          </Group>
        ))}
      </Stack>
    </div>
  );
}

export default WorldStudioAlerts;
