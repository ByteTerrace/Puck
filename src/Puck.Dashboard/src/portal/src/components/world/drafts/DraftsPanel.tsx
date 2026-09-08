import { useEffect, useState } from "react";
import { Badge, Button, Group, Stack, Text } from "@mantine/core";
import { StudioContext, useStudioIsDirty } from "../../../context/StudioContext";
import { useStudioConfirmation } from "../StudioConfirmation";
import { readDraftListing } from "../../../document/localDrafts";

/**
 * The local draft library — replaces the old studio's `vault/` (a cloud-backed
 * `WorldStorageClient` document vault this studio has no equivalent of; local drafts are this
 * studio's only offline document library, `document/localDrafts.ts`'s own `LocalDraftStore`).
 * Bound entirely to `StudioContext`. Re-reads the store when a save/delete operation finishes.
 */
export function DraftsPanel() {
  const actor = StudioContext.useActorRef();
  const isDirty = useStudioIsDirty();
  const canDelete = StudioContext.useSelector(snapshot => snapshot.matches({ ready: { document: "idle" } }));
  const confirm = useStudioConfirmation();
  const [{ drafts, error }, setListing] = useState(() => readDraftListing(actor.getSnapshot().context.machineInput.draftStore));

  useEffect(() => {
    const refresh = () => setListing(readDraftListing(actor.getSnapshot().context.machineInput.draftStore));
    refresh();
    let wasWriting = false;
    const subscription = actor.subscribe(snapshot => {
      const writing = snapshot.matches({ ready: { document: "savingDraft" } }) || snapshot.matches({ ready: { document: "deletingDraft" } });
      if (wasWriting && !writing) refresh();
      wasWriting = writing;
    });
    return () => subscription.unsubscribe();
  }, [actor]);

  return (
    <details className="studio-diagnostics">
      <summary>Local drafts <span>{drafts.length} saved</span></summary>
      <Stack gap="xs" mt="sm">
        {error && <Text size="sm" c="red" role="alert">{error}</Text>}
        {!error && drafts.length === 0 && <Text size="sm" c="dimmed">No local drafts saved yet — use "Save draft" in the header.</Text>}
        {drafts.map((draft) => (
          <Group key={draft.id} justify="space-between" wrap="nowrap">
            <div>
              <Text size="sm" fw={600}>{draft.title}</Text>
              <Text size="xs" c="dimmed">{draft.documentName} · {draft.revisions.length} revision{draft.revisions.length === 1 ? "" : "s"} · saved {draft.revisions[0]?.savedAt.slice(0, 10) ?? "—"}</Text>
            </div>
            <Group gap={6}>
              <Badge size="xs" variant="light">{draft.id}</Badge>
              <Button size="compact-xs" variant="default" onClick={async () => {
                if (!isDirty || await confirm("Discard unsaved edits and load this draft?")) actor.send({ type: "LOAD_DRAFT", id: draft.id });
              }}>Load</Button>
              <Button size="compact-xs" variant="subtle" color="red" disabled={!canDelete} onClick={async () => {
                if (await confirm(`Delete the local draft “${draft.title}” and its saved revisions?`)) actor.send({ type: "DELETE_DRAFT", id: draft.id });
              }}>Delete</Button>
            </Group>
          </Group>
        ))}
      </Stack>
    </details>
  );
}

export default DraftsPanel;
