import type { ComponentType } from "react";
import { useMemo, useState } from "react";
import { Button, Group, Pagination, Table, Text, TextInput } from "@mantine/core";
import { getAt, pathToEnginePath, type JsonPath } from "../document/jsonPath";
import type { SchemaWalker } from "./schemaWalk";
// Type-only: SchemaNode.tsx is the one file that imports CellsTable (to special-case a state
// row's cells array), so CellsTable takes it back as the `renderValue` PROP below rather than
// importing the component itself — an ordinary top-level import cycle between these two
// modules would hand CellsTable an undefined `SchemaNode` reference at load time under CommonJS
// (each module's `require` of the other resolves to the other's still-empty `exports` while it
// is mid-initialization). `import type` is erased entirely at compile time, so it never
// participates in that cycle.
import type { DocumentEdit } from "./SchemaNode";

/** The shape of `SchemaNode` itself — `CellsTable` never imports it directly (see above); its caller hands the component in. */
export type SchemaFieldRenderer = ComponentType<{
  readonly walker: SchemaWalker;
  readonly document: unknown;
  readonly path: JsonPath;
  readonly onEdit: (edit: DocumentEdit) => void;
  readonly compact?: boolean;
}>;

const PAGE_SIZE = 64;

interface CellRow {
  readonly key: string;
  readonly index: number; // this cell's position in the authored `cells` array
}

/** True when every authored key parses as a (possibly negative) whole number — the sort this table uses to read like a board or a hand rather than authoring order. */
function allKeysNumeric(rows: readonly CellRow[]): boolean {
  return rows.length > 0 && rows.every(row => /^-?\d+$/.test(row.key));
}

/**
 * The paged key/value editor for a state row's `cells` array. `SchemaNode` renders this in
 * place of the generic array editor when `walker.isCellsField(node)` says a node is the
 * `cells` array of a `state.world`/`body`/`identity` row (see the comment on that check in
 * `schemaWalk.ts` — it keys off the row's schema location, not this row's authored name).
 */
export function CellsTable({ walker, document, path, onEdit, label, renderValue: RenderValue }: {
  readonly walker: SchemaWalker;
  readonly document: unknown;
  readonly path: JsonPath;
  readonly onEdit: (edit: DocumentEdit) => void;
  readonly label: string;
  readonly renderValue: SchemaFieldRenderer;
}) {
  const cells: readonly Record<string, unknown>[] = Array.isArray(getAt(document, path)) ? (getAt(document, path) as Record<string, unknown>[]) : [];
  const rows = useMemo<CellRow[]>(() => cells.map((cell, index) => ({ key: String(cell.key ?? ""), index })), [cells]);
  const ordered = useMemo(() => {
    if (allKeysNumeric(rows)) return [...rows].sort((a, b) => Number(a.key) - Number(b.key));
    return rows;
  }, [rows]);

  const [page, setPage] = useState(1);
  const pageCount = Math.max(1, Math.ceil(ordered.length / PAGE_SIZE));
  const currentPage = Math.min(page, pageCount);
  const visible = ordered.slice((currentPage - 1) * PAGE_SIZE, currentPage * PAGE_SIZE);

  const [newKey, setNewKey] = useState("");

  const existingKeys = new Set(rows.map(row => row.key));

  function addCell() {
    const key = newKey.trim();
    if (!key || existingKeys.has(key)) return;
    const template = walker.atPath([...path, cells.length], document);
    const defaults = walker.defaultFor(template) as Record<string, unknown>;
    onEdit({ path, value: [...cells, { ...defaults, key }], label: `add cells[${key}] to ${pathToEnginePath(path)}` });
    setNewKey("");
  }

  function removeCell(index: number) {
    onEdit({ path, value: cells.filter((_, i) => i !== index), label: `remove cells[${index}] from ${pathToEnginePath(path)}` });
  }

  return (
    <div>
      <Group justify="space-between" mb={4}>
        <Text size="sm" fw={500}>{label} ({cells.length})</Text>
        {pageCount > 1 && <Pagination size="xs" total={pageCount} value={currentPage} onChange={setPage} />}
      </Group>
      <Table striped withTableBorder>
        <Table.Thead>
          <Table.Tr><Table.Th>Key</Table.Th><Table.Th>Value</Table.Th><Table.Th /></Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {visible.map(row => (
            <Table.Tr key={row.index}>
              <Table.Td>{row.key}</Table.Td>
              <Table.Td>
                <RenderValue walker={walker} document={document} path={[...path, row.index, "value"]} onEdit={onEdit} compact />
              </Table.Td>
              <Table.Td>
                <Button size="compact-xs" variant="subtle" color="red" onClick={() => removeCell(row.index)}>Remove</Button>
              </Table.Td>
            </Table.Tr>
          ))}
          {!visible.length && (
            <Table.Tr><Table.Td colSpan={3}><Text size="xs" c="dimmed">No cells authored.</Text></Table.Td></Table.Tr>
          )}
        </Table.Tbody>
      </Table>
      <Group mt="xs" gap="xs" align="end">
        <TextInput size="xs" label="New cell key" value={newKey} onChange={e => setNewKey(e.currentTarget.value)} />
        <Button size="compact-xs" variant="default" disabled={!newKey.trim() || existingKeys.has(newKey.trim())} onClick={addCell}>
          Add cell
        </Button>
      </Group>
    </div>
  );
}
