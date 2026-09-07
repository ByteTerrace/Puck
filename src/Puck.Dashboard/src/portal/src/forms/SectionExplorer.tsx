import { useMemo } from "react";
import { Badge, Group, Stack, Text, UnstyledButton } from "@mantine/core";
import { getAt, type JsonPath } from "../document/jsonPath";
import { resolve, type JsonSchema } from "./schemaWalk";

/**
 * Lists every root section (`walker.rootSections()`), in schema order, each with a presence
 * badge and its description. Nothing is hidden — `imports`/`exports` (the fragment-composition
 * surface) render exactly like any other section, since a document author editing them through
 * this form is exactly as valid as editing `motion` or `state`.
 */
export function SectionExplorer({ bundle, document, selected, onSelect }: {
  readonly bundle: JsonSchema;
  readonly document: unknown;
  readonly selected?: JsonPath;
  readonly onSelect: (path: JsonPath) => void;
}) {
  const walker = useMemo(() => resolve(bundle), [bundle]);
  const sections = walker.rootSections();

  return (
    <Stack gap={4}>
      {sections.map(section => {
        const authored = getAt(document, section.path) !== undefined;
        const isSelected = selected?.length === 1 && selected[0] === section.key;
        return (
          <UnstyledButton
            key={section.key}
            className="studio-explorer-item"
            aria-pressed={isSelected}
            onClick={() => onSelect(section.path)}
          >
            <Group justify="space-between" wrap="nowrap" gap="xs">
              <Text size="sm" fw={isSelected ? 600 : 400}>{section.title ?? section.key}</Text>
              <Badge size="xs" color={authored ? "teal" : "gray"} variant="light">{authored ? "authored" : "absent"}</Badge>
            </Group>
            {section.description && <Text size="xs" c="dimmed" lineClamp={2}>{section.description}</Text>}
          </UnstyledButton>
        );
      })}
    </Stack>
  );
}
