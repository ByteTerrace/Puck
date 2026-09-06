import React, { useState } from "react";
import { Alert, Button, Card, FileButton, Group, Stack, Text, Textarea } from "@mantine/core";
import { inspectWorldDocument, MAX_DOCUMENT_BYTES } from "../../engine/documentValidation";
export interface WorldWorkbenchProps {
  worldJson: string;
  draft: string;
  onDraftChange: (text: string) => void;
  onWorldJsonChange: (text: string) => void;
}
export const WorldWorkbench: React.FC<WorldWorkbenchProps> = ({ worldJson, draft, onDraftChange, onWorldJsonChange }) => {
  const [message, setMessage] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const check = (apply: boolean) => {
    try {
      const { previewIssues } = inspectWorldDocument(draft);
      setFailed(false);
      setMessage(previewIssues.length ? "Structural checks passed. Preview limitations: " + previewIssues.join(" ") : "Structural checks passed. Native engine validation has not been run.");
      if(apply)
        onWorldJsonChange(draft);
    }
    catch(error) {
      setFailed(true);
      setMessage((error as Error).message);
    }
  };
  const download = () => {
    const url = URL.createObjectURL(new Blob([draft], { type: "application/json" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "world.json";
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };
  return <Card
    withBorder
    radius="md"
    p="md"><Stack
      gap="sm">
      <Group
        justify="space-between"><Text
          component="h2"
          fw={650}
          size="md">World document</Text>
        <Group
          gap="xs"><FileButton
            accept=".json,application/json"
            onChange={async (file) => {
              if(!file)
                return;
              if(file.size > MAX_DOCUMENT_BYTES) {
                setFailed(true);
                setMessage("Documents are limited to 2 MB.");
                return;
              }
              try {
                onDraftChange(await file.text());
                setMessage(null);
              }
              catch {
                setFailed(true);
                setMessage("Could not read this file.");
              }
            }}>{props => <Button
              {...props}
              variant="default">Import</Button>}</FileButton><Button
                variant="default"
                onClick={download}>Export JSON</Button></Group>
      </Group>
      <Text
        size="sm"
        c="var(--ink-soft)">Edit your document, then apply it to restart preview. Exports contain the editor text; local saves contain the applied document.</Text>
      <Textarea
        label="World JSON"
        value={draft}
        onChange={event => { onDraftChange(event.currentTarget.value); setMessage(null); }}
        spellCheck={false}
        rows={22}
        styles={{ input: { fontFamily: "ui-monospace, monospace", fontSize: 13, lineHeight: 1.55, resize: "vertical", background: "var(--code-bg)", color: "var(--code-fg)" } }} />
      <Group><Button
        onClick={() => check(true)}
        disabled={draft === worldJson}>Apply document</Button><Button
          variant="default"
          onClick={() => check(false)}>Check document</Button><Text
            size="xs"
            c="var(--ink-soft)">{draft === worldJson ? "Applied" : "Unapplied edits"}</Text></Group>
      {message && <Alert
        role="status"
        color={failed ? "red" : "gray"}>{message}</Alert>}
    </Stack></Card>;
};
export default WorldWorkbench;
