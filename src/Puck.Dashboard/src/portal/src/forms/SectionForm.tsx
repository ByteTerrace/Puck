import { useMemo } from "react";
import { Button, Group, Stack, Text, Title } from "@mantine/core";
import { getAt, type JsonPath } from "../document/jsonPath";
import { SchemaNode, type DocumentEdit } from "./SchemaNode";
import { resolve, type JsonSchema } from "./schemaWalk";

/**
 * One root section (`path` is always a single-segment path, e.g. `["motion"]`), rendered as a
 * header (title + description, falling back to the section's own key when the bundle carries
 * neither) plus that section's `SchemaNode`. A nullable, absent section (every root section is
 * nullable — `WorldDefinition`'s members are all optional) renders `SchemaNode`'s own
 * "Set a value" affordance rather than anything special here.
 */
export function SectionForm({ bundle, document, path, onEdit, onRevealJson }: {
  readonly bundle: JsonSchema;
  readonly document: unknown;
  readonly path: JsonPath;
  readonly onEdit: (edit: DocumentEdit) => void;
  readonly onRevealJson?: (path: JsonPath) => void;
}) {
  const walker = useMemo(() => resolve(bundle), [bundle]);
  const node = walker.atPath(path, document);
  const title = walker.title(node) ?? formatSectionKey(path[path.length - 1]);
  const description = walker.description(node);
  const authored = getAt(document, path) !== undefined;

  return (
    <Stack gap="sm">
      <Group justify="space-between" align="flex-start">
        <div>
          <Title order={3} size="h4">{title}</Title>
          {description && <Text size="sm" c="dimmed" maw={640}>{description}</Text>}
        </div>
        <Group gap="xs">
          <Text size="xs" c={authored ? "teal" : "dimmed"}>{authored ? "Authored" : "Absent"}</Text>
          {onRevealJson && <Button size="compact-xs" variant="default" onClick={() => onRevealJson(path)}>Reveal in JSON</Button>}
        </Group>
      </Group>
      <SchemaNode walker={walker} document={document} path={path} onEdit={onEdit} compact label={title} />
    </Stack>
  );
}

function formatSectionKey(key: string | number | undefined): string {
  if (key === undefined) return "Document";
  return String(key).replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/^./, c => c.toUpperCase());
}
