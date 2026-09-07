import { useEffect, useState } from "react";
import { Badge, Button, Group, Stack, Text } from "@mantine/core";
import { StudioContext } from "../../../context/StudioContext";
import type { StudioDraft } from "../../../document/localDrafts";

/**
 * The local draft library — replaces the old studio's `vault/` (a cloud-backed
 * `WorldStorageClient` document vault this studio has no equivalent of; local drafts are this
 * studio's only offline document library, `document/localDrafts.ts`'s own `LocalDraftStore`).
 * Bound entirely to `StudioContext` — no props. Re-reads the store on every actor transition
 * (`SAVE_DRAFT`/`DELETE_DRAFT`/`LOAD_DRAFT` all resolve through the machine, so the store's own
 * content and this list can drift apart between transitions but never across one).
 */
export function DraftsPanel() {
  const actor = StudioContext.useActorRef();
  const [drafts, setDrafts] = useState<StudioDraft[]>(() => actor.getSnapshot().context.machineInput.draftStore.list());

  useEffect(() => {
    const refresh = () => setDrafts(actor.getSnapshot().context.machineInput.draftStore.list());
    refresh();
    const subscription = actor.subscribe(refresh);
    return () => subscription.unsubscribe();
  }, [actor]);

  return (
    <details className="studio-diagnostics">
      <summary>Local drafts <span>{drafts.length} saved</span></summary>
      <Stack gap="xs" mt="sm">
        {drafts.length === 0 && <Text size="sm" c="dimmed">No local drafts saved yet — use "Save draft" in the header.</Text>}
        {drafts.map((draft) => (
          <Group key={draft.id} justify="space-between" wrap="nowrap">
            <div>
              <Text size="sm" fw={600}>{draft.title}</Text>
              <Text size="xs" c="dimmed">{draft.documentName} · {draft.revisions.length} revision{draft.revisions.length === 1 ? "" : "s"} · saved {draft.revisions[0]?.savedAt.slice(0, 10) ?? "—"}</Text>
            </div>
            <Group gap={6}>
              <Badge size="xs" variant="light">{draft.id}</Badge>
              <Button size="compact-xs" variant="default" onClick={() => actor.send({ type: "LOAD_DRAFT", id: draft.id })}>Load</Button>
              <Button size="compact-xs" variant="subtle" color="red" onClick={() => actor.send({ type: "DELETE_DRAFT", id: draft.id })}>Delete</Button>
            </Group>
          </Group>
        ))}
      </Stack>
    </details>
  );
}

export default DraftsPanel;
