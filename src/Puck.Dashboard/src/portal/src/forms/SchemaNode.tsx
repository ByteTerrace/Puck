import { useState } from "react";
import { Button, Group, NumberInput, Select, Stack, Switch, Text, TextInput, Textarea } from "@mantine/core";
import { getAt, pathToEnginePath, type JsonPath } from "../document/jsonPath";
import { classify, computeArmSwitch, computeArrayMove, type SchemaWalker, type SchemaNode as WalkedNode } from "./schemaWalk";
import { CellsTable } from "./CellsTable";

/**
 * One document edit. `value === undefined` means "remove whatever is at `path`" (a real JSON
 * value is never `undefined`, so this reads unambiguously) — a caller applies an edit as:
 *
 * ```ts
 * setDocument(doc => edit.value === undefined ? deleteAt(doc, edit.path) : setAt(doc, edit.path, edit.value));
 * ```
 *
 * `label` is a short human sentence describing the edit ("set state.world[2].min"), suitable
 * for an undo-history entry or an audit line.
 */
export interface DocumentEdit {
  readonly path: JsonPath;
  readonly value: unknown;
  readonly label: string;
}

export interface SchemaNodeProps {
  readonly walker: SchemaWalker;
  readonly document: unknown;
  readonly path: JsonPath;
  readonly onEdit: (edit: DocumentEdit) => void;
  /** Overrides the field header text; defaults to a formatted form of the last path segment. */
  readonly label?: string;
  /** Renders without its own header/description, for embedding inline (e.g. a fixed-arity vector's own components, or a table cell). */
  readonly compact?: boolean;
  /** How many nested $ref-driven object levels of RECURSION already lie above this node — internal, do not pass. */
  readonly refDepth?: number;
}

const REF_COLLAPSE_DEPTH = 3;

function formatKey(key: string | number | undefined): string {
  if (key === undefined) return "";
  if (typeof key === "number") return `#${key}`;
  return key
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/^./, c => c.toUpperCase());
}

function editLabel(action: string, path: JsonPath): string {
  return `${action} ${pathToEnginePath(path) || "document"}`;
}

/** Renders any schema node over Mantine inputs, recursing into objects, arrays, and unions. */
export function SchemaNode(props: SchemaNodeProps) {
  const { walker, document, path, onEdit, label, compact, refDepth = 0 } = props;
  const node = walker.atPath(path, document);
  const value = getAt(document, path);
  const nullable = walker.isNullable(node);
  const fieldLabel = label ?? formatKey(path[path.length - 1]);

  if (nullable && value === null) {
    return (
      <Group gap="xs" align="center">
        {!compact && <Text size="sm" c="dimmed">{fieldLabel}: not set</Text>}
        <Button size="compact-xs" variant="default" onClick={() => onEdit({ path, value: walker.defaultFor(node), label: editLabel("set", path) })}>
          {compact ? "Set" : "Set a value"}
        </Button>
      </Group>
    );
  }

  const body = <SchemaNodeBody {...props} node={node} value={value} fieldLabel={fieldLabel} refDepth={refDepth} />;
  if (!nullable) return body;
  return (
    <Stack gap={4}>
      {body}
      <Button size="compact-xs" variant="subtle" onClick={() => onEdit({ path, value: null, label: editLabel("clear", path) })}>
        Clear (set to null)
      </Button>
    </Stack>
  );
}

interface BodyProps extends SchemaNodeProps {
  node: WalkedNode;
  value: unknown;
  fieldLabel: string;
}

function SchemaNodeBody({ walker, document, path, onEdit, compact, refDepth = 0, node, value, fieldLabel }: BodyProps) {
  const kind = classify(node);
  const description = walker.description(node);

  switch (kind) {
    case "enum": {
      const values = walker.enumValues(node) ?? [];
      const fixed = values.length <= 1 && typeof node.schema.const === "string";
      return (
        <Select
          label={compact ? undefined : fieldLabel}
          description={compact ? undefined : description}
          data={[...values]}
          value={typeof value === "string" ? value : null}
          disabled={fixed}
          allowDeselect={false}
          onChange={v => v != null && onEdit({ path, value: v, label: editLabel("set", path) })}
        />
      );
    }
    case "string": {
      const raw = typeof value === "string" ? value : "";
      return (
        <TextInput
          label={compact ? undefined : fieldLabel}
          description={compact ? undefined : description}
          placeholder={typeof node.schema.pattern === "string" ? `Pattern: ${node.schema.pattern}` : undefined}
          value={raw}
          onChange={e => onEdit({ path, value: e.currentTarget.value, label: editLabel("set", path) })}
        />
      );
    }
    case "boolean": {
      return (
        <Switch
          label={compact ? undefined : fieldLabel}
          description={compact ? undefined : description}
          checked={value === true}
          onChange={e => onEdit({ path, value: e.currentTarget.checked, label: editLabel("set", path) })}
        />
      );
    }
    case "integer":
    case "number": {
      // A JS number past Number.isSafeInteger (or a value some upstream step already kept as a
      // string token) cannot round-trip through Mantine's numeric parsing without silently
      // rounding it — the same reason engine/documentValidation.ts refuses a document carrying
      // one rather than pretending to preserve it. Keep the raw token intact and defer editing
      // it to the JSON draft instead of corrupting it here.
      const unsafe =
        value !== undefined &&
        value !== null &&
        (typeof value === "string" || (typeof value === "number" && kind === "integer" && !Number.isSafeInteger(value)));
      if (unsafe) {
        return (
          <TextInput
            label={compact ? undefined : fieldLabel}
            description={compact ? undefined : "Beyond exact precision here. Edit this value in the JSON draft."}
            value={String(value)}
            readOnly
          />
        );
      }
      const numeric = typeof value === "number" ? value : "";
      return (
        <NumberInput
          label={compact ? undefined : fieldLabel}
          description={compact ? undefined : description}
          allowDecimal={kind === "number"}
          hideControls
          value={numeric}
          onChange={v => {
            if (v === "") return;
            const n = typeof v === "number" ? v : Number(v);
            if (!Number.isFinite(n)) return;
            if (kind === "integer" && !Number.isSafeInteger(n)) return; // never silently round-trip a lossy edit
            onEdit({ path, value: n, label: editLabel("set", path) });
          }}
        />
      );
    }
    case "union": {
      const arms = node.union!.arms;
      return (
        <Stack gap={4}>
          {!compact && description && <Text size="xs" c="dimmed">{description}</Text>}
          <Select
            label={compact ? undefined : fieldLabel}
            placeholder="Choose a type…"
            data={arms.map(arm => ({ value: arm.value, label: arm.value }))}
            value={node.union!.selected ?? null}
            onChange={v => v != null && onEdit({ path, value: computeArmSwitch(walker, node, v), label: editLabel(`set ${node.union!.key} to ${v} on`, path) })}
          />
        </Stack>
      );
    }
    case "array": {
      if (walker.isCellsField(node)) {
        return <CellsTable walker={walker} document={document} path={path} onEdit={onEdit} label={fieldLabel} renderValue={SchemaNode} />;
      }
      return <ArrayField walker={walker} document={document} path={path} onEdit={onEdit} node={node} fieldLabel={fieldLabel} compact={compact} refDepth={refDepth} />;
    }
    case "object": {
      if (refDepth >= REF_COLLAPSE_DEPTH) {
        return <CollapsedObject walker={walker} document={document} path={path} onEdit={onEdit} fieldLabel={fieldLabel} />;
      }
      return <ObjectField walker={walker} document={document} onEdit={onEdit} node={node} fieldLabel={fieldLabel} compact={compact} refDepth={refDepth} />;
    }
    default:
      return <JsonFallback document={document} path={path} onEdit={onEdit} fieldLabel={fieldLabel} compact={compact} description={description} />;
  }
}

function ArrayField({ walker, document, path, onEdit, node, fieldLabel, compact, refDepth }: {
  walker: SchemaWalker; document: unknown; path: JsonPath; onEdit: (edit: DocumentEdit) => void;
  node: WalkedNode; fieldLabel: string; compact?: boolean; refDepth: number;
}) {
  const schema = node.schema;
  const items: readonly unknown[] = Array.isArray(getAt(document, path)) ? (getAt(document, path) as unknown[]) : [];
  const entries = walker.children(node, document);
  const fixedArity = typeof schema.minItems === "number" && schema.minItems === schema.maxItems && !Array.isArray(schema.items);

  if (fixedArity) {
    return (
      <Stack gap={4}>
        {!compact && <Text size="sm" fw={500}>{fieldLabel}</Text>}
        <Group gap="xs">
          {entries.map(entry => (
            <SchemaNode key={entry.key} walker={walker} document={document} path={entry.path} onEdit={onEdit} compact refDepth={refDepth} />
          ))}
        </Group>
      </Stack>
    );
  }

  const itemSchema = Array.isArray(schema.items) ? {} : (schema.items ?? {});
  return (
    <Stack gap={6}>
      {!compact && <Text size="sm" fw={500}>{fieldLabel} ({items.length})</Text>}
      {entries.map((entry, index) => (
        <Group key={index} align="flex-start" gap="xs" wrap="nowrap">
          <div style={{ flex: 1, minWidth: 0 }}>
            <SchemaNode walker={walker} document={document} path={entry.path} onEdit={onEdit} refDepth={refDepth + 1} />
          </div>
          <Button size="compact-xs" variant="subtle" disabled={index === 0}
            onClick={() => onEdit({ path, value: computeArrayMove(items, index, -1), label: editLabel(`move [${index}] up in`, path) })}>
            ↑
          </Button>
          <Button size="compact-xs" variant="subtle" disabled={index === items.length - 1}
            onClick={() => onEdit({ path, value: computeArrayMove(items, index, 1), label: editLabel(`move [${index}] down in`, path) })}>
            ↓
          </Button>
          <Button size="compact-xs" variant="subtle" color="red"
            onClick={() => onEdit({ path, value: items.filter((_, i) => i !== index), label: editLabel(`remove [${index}] from`, path) })}>
            Remove
          </Button>
        </Group>
      ))}
      <Button size="compact-xs" variant="default"
        onClick={() => onEdit({ path, value: [...items, walker.defaultFor(walker.atPath([...path, items.length], document))], label: editLabel("add to", path) })}>
        Add
      </Button>
      {!items.length && Object.keys(itemSchema).length === 0 && <Text size="xs" c="dimmed">Empty.</Text>}
    </Stack>
  );
}

function ObjectField({ walker, document, onEdit, node, fieldLabel, compact, refDepth }: {
  walker: SchemaWalker; document: unknown; onEdit: (edit: DocumentEdit) => void;
  node: WalkedNode; fieldLabel: string; compact?: boolean; refDepth: number;
}) {
  const entries = walker.children(node, document);
  return (
    <Stack gap={6} className="schema-object-field">
      {!compact && <Text size="sm" fw={500}>{fieldLabel}</Text>}
      {entries.map(entry => {
        const present = getAt(document, entry.path) !== undefined;
        if (!entry.required && !present) {
          return (
            <Group key={String(entry.key)} gap="xs">
              <Text size="xs" c="dimmed">{formatKey(entry.key)}</Text>
              <Button size="compact-xs" variant="subtle"
                onClick={() => onEdit({ path: entry.path, value: walker.defaultFor(entry.node), label: editLabel("add", entry.path) })}>
                Add
              </Button>
            </Group>
          );
        }
        return (
          <Group key={String(entry.key)} align="flex-start" gap="xs" wrap="nowrap">
            <div style={{ flex: 1, minWidth: 0 }}>
              <SchemaNode walker={walker} document={document} path={entry.path} onEdit={onEdit} refDepth={refDepth + 1} />
            </div>
            {!entry.required && (
              <Button size="compact-xs" variant="subtle" color="red"
                onClick={() => onEdit({ path: entry.path, value: undefined, label: editLabel("remove", entry.path) })}>
                Remove
              </Button>
            )}
          </Group>
        );
      })}
      {!entries.length && <Text size="xs" c="dimmed">No fields.</Text>}
    </Stack>
  );
}

/** A `$ref`-recursive shape (a predicate tree, nested effects, …) collapsed past {@link REF_COLLAPSE_DEPTH} levels, with an explicit expand toggle so a self-referential schema never forces an unbounded render tree. */
function CollapsedObject({ walker, document, path, onEdit, fieldLabel }: {
  walker: SchemaWalker; document: unknown; path: JsonPath; onEdit: (edit: DocumentEdit) => void; fieldLabel: string;
}) {
  const [expanded, setExpanded] = useState(false);
  if (expanded) {
    return (
      <Stack gap={4}>
        <Button size="compact-xs" variant="subtle" onClick={() => setExpanded(false)}>Collapse {fieldLabel}</Button>
        <SchemaNode walker={walker} document={document} path={path} onEdit={onEdit} refDepth={0} />
      </Stack>
    );
  }
  return (
    <Group gap="xs">
      <Text size="xs" c="dimmed">{fieldLabel} (nested)</Text>
      <Button size="compact-xs" variant="default" onClick={() => setExpanded(true)}>Expand</Button>
    </Group>
  );
}

/** The fallback for a shape this form does not model precisely (a dictionary, a literal-or-state-reference union, `true`/`{}` schemas): a raw JSON textarea, parsed on blur. */
function JsonFallback({ document, path, onEdit, fieldLabel, compact, description }: {
  document: unknown; path: JsonPath; onEdit: (edit: DocumentEdit) => void; fieldLabel: string; compact?: boolean; description?: string;
}) {
  const current = getAt(document, path);
  const [text, setText] = useState(() => JSON.stringify(current, null, 2) ?? "");
  const [error, setError] = useState<string | null>(null);
  return (
    <Stack gap={4}>
      {!compact && <Text size="sm" fw={500}>{fieldLabel}</Text>}
      {!compact && description && <Text size="xs" c="dimmed">{description}</Text>}
      <Textarea autosize minRows={2} maxRows={12} value={text}
        onChange={e => { setText(e.currentTarget.value); setError(null); }}
        onBlur={() => {
          if (!text.trim()) { onEdit({ path, value: undefined, label: editLabel("remove", path) }); return; }
          try {
            const parsed = JSON.parse(text);
            onEdit({ path, value: parsed, label: editLabel("set", path) });
          } catch {
            setError("Not valid JSON — the previous value is kept until this parses.");
          }
        }}
      />
      {error && <Text size="xs" c="red">{error}</Text>}
    </Stack>
  );
}
