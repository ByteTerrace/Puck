import { useState } from "react";
import { Button, Group, Stack, Text, TextInput, Textarea } from "@mantine/core";
import { pathToEnginePath } from "../document/jsonPath";
import type { SchemaWalker } from "./schemaWalk";
import type { DocumentEdit } from "./SchemaNode";

/**
 * The root document's own reserved-prefix "extensions" bag. The bundle carries no named
 * `extensions` property for this: the ROOT schema declares `additionalProperties: false` plus
 * `patternProperties: {"^[$_]": true}` — any top-level key starting with `$` or `_` is
 * accepted with an unconstrained (`true`) schema, and every other unknown top-level key is
 * refused. So this editor's model is simply: every root-level document key that is not one of
 * `walker.rootSections()`'s declared keys IS an extension entry, editable as raw JSON.
 */
export function ExtensionsEditor({ walker, document, onEdit }: {
  readonly walker: SchemaWalker;
  readonly document: unknown;
  readonly onEdit: (edit: DocumentEdit) => void;
}) {
  const known = new Set(walker.rootSections().map(section => section.key));
  const root = document && typeof document === "object" && !Array.isArray(document) ? (document as Record<string, unknown>) : {};
  const entries = Object.keys(root).filter(key => !known.has(key));

  const [newKey, setNewKey] = useState("");
  const trimmedNewKey = newKey.trim();
  const keyTaken = entries.includes(trimmedNewKey) || known.has(trimmedNewKey);

  return (
    <Stack gap="sm">
      <Text size="sm" c="dimmed">
        Reserved-prefix (<code>$</code>/<code>_</code>) top-level keys the schema does not otherwise name. This is the
        one place a document may carry a key none of its declared sections recognize.
      </Text>
      {entries.map(key => (
        <ExtensionEntry key={key} document={root} entryKey={key} onEdit={onEdit} />
      ))}
      {!entries.length && <Text size="xs" c="dimmed">No extensions authored.</Text>}
      <Group gap="xs" align="end">
        <TextInput
          size="xs"
          label="New key"
          description="Must be non-empty; a $ or _ prefix is what makes it an extension key rather than an unmapped one."
          value={newKey}
          onChange={e => setNewKey(e.currentTarget.value)}
        />
        <Button
          size="compact-xs"
          variant="default"
          disabled={!trimmedNewKey || keyTaken}
          onClick={() => {
            onEdit({ path: [trimmedNewKey], value: null, label: `add ${pathToEnginePath([trimmedNewKey])}` });
            setNewKey("");
          }}
        >
          Add extension
        </Button>
      </Group>
    </Stack>
  );
}

function ExtensionEntry({ document, entryKey, onEdit }: {
  readonly document: Record<string, unknown>;
  readonly entryKey: string;
  readonly onEdit: (edit: DocumentEdit) => void;
}) {
  const [text, setText] = useState(() => JSON.stringify(document[entryKey], null, 2) ?? "null");
  const [error, setError] = useState<string | null>(null);
  return (
    <Group align="flex-start" gap="xs" wrap="nowrap">
      <Text size="sm" style={{ minWidth: 120 }}>{entryKey}</Text>
      <div style={{ flex: 1 }}>
        <Textarea
          autosize
          minRows={1}
          maxRows={10}
          value={text}
          onChange={e => { setText(e.currentTarget.value); setError(null); }}
          onBlur={() => {
            try {
              const parsed = JSON.parse(text);
              onEdit({ path: [entryKey], value: parsed, label: `set ${pathToEnginePath([entryKey])}` });
            } catch {
              setError("Not valid JSON — the previous value is kept until this parses.");
            }
          }}
        />
        {error && <Text size="xs" c="red">{error}</Text>}
      </div>
      <Button
        size="compact-xs"
        variant="subtle"
        color="red"
        onClick={() => onEdit({ path: [entryKey], value: undefined, label: `remove ${pathToEnginePath([entryKey])}` })}
      >
        Remove
      </Button>
    </Group>
  );
}
