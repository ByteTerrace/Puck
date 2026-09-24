import { Alert, Button, Group, Paper, ScrollArea, Stack, Table, Text, Title } from "@mantine/core";
import { RiErrorWarningLine, RiPlayLine } from "@remixicon/react";
import { Kicker } from "../../ui/Kicker";
import classes from "../Page.module.css";
import SqlEditor, { type SqlFileCompletion } from "../SqlEditor";
import type { QueryOutput } from "./queryResult";
import { Well } from "../../ui/Well";

interface QueryPanelProps {
  files: SqlFileCompletion[];
  isRunning: boolean;
  onChange: (sql: string) => void;
  onRun: () => void;
  output: QueryOutput | undefined;
  queryError: string | undefined;
  sql: string;
}

function QueryResults({ output }: { output: QueryOutput }) {
  return (
    <Stack gap="xs">
      <Text c="dimmed" className={classes.data} role="status">
        {output.truncated
          ? `First ${output.rows.length.toLocaleString()} rows shown; the query returned more. Add a LIMIT or an aggregate to see the rest.`
          : `${output.rows.length.toLocaleString()} row(s)`}
      </Text>
      <Well>
        <ScrollArea.Autosize mah={480} type="auto">
          <Table striped>
            <Table.Thead>
              <Table.Tr>
                {output.columns.map((column, columnIndex) => (
                  // Two columns may share a name (`SELECT 1 AS x, 2 AS x`); position is their identity.
                  <Table.Th key={columnIndex}>{column}</Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {output.rows.map((row, rowIndex) => (
                <Table.Tr key={rowIndex}>
                  {row.map((cell, cellIndex) => (
                    <Table.Td className={`${classes.data} ${classes.nowrap}`} key={cellIndex}>
                      {null === cell ? (
                        <Text c="dimmed" component="span" fs="italic" inherit>
                          NULL
                        </Text>
                      ) : (
                        cell
                      )}
                    </Table.Td>
                  ))}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </ScrollArea.Autosize>
      </Well>
    </Stack>
  );
}

/** SQL over the user's files, run by DuckDB in the browser, and the table it answers with. */
export function QueryPanel({ files, isRunning, onChange, onRun, output, queryError, sql }: QueryPanelProps) {
  return (
    <Paper p="md" withBorder>
      <Stack gap="md">
        <Group justify="space-between" wrap="nowrap">
          <div>
            <Kicker>DuckDB, in your browser</Kicker>
            <Title mt={4} order={2} size="h4">
              SQL
            </Title>
          </div>
          <Button
            disabled={0 === sql.trim().length}
            leftSection={<RiPlayLine size={16} />}
            loading={isRunning}
            onClick={onRun}
            size="sm"
          >
            Run
          </Button>
        </Group>
        <SqlEditor files={files} onChange={onChange} value={sql} />
        {queryError ? (
          <Alert color="red" icon={<RiErrorWarningLine size={18} />} title="Query failed" variant="light">
            {queryError}
          </Alert>
        ) : null}
        {output ? <QueryResults output={output} /> : null}
      </Stack>
    </Paper>
  );
}
