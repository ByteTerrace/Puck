import { useState } from "react";
import {
  Alert,
  Badge,
  Button,
  Group,
  Modal,
  Paper,
  ScrollArea,
  Stack,
  Text,
  TextInput,
  Title,
  UnstyledButton,
} from "@mantine/core";
import { RiDownloadLine, RiDraftLine, RiErrorWarningLine, RiFolderOpenLine, RiSaveLine } from "@remixicon/react";
import {
  StudioContext,
  useStudioBoot,
  useStudioCanSaveDraft,
  useStudioChecking,
  useStudioIsDirty,
  useStudioLanguage,
  useStudioOfficial,
  useStudioProblems,
  useStudioWorkspace,
  useStudioWorld,
} from "../../context/StudioContext";
import { readDraftListing } from "../../document/localDrafts";
import { describeTree, type DocumentRole, type ManifestDocumentEntry } from "../../official/manifest";
import { EmptyState } from "../../ui/EmptyState";
import { Kicker } from "../../ui/Kicker";
import { useStudioConfirmation } from "./StudioConfirmation";
import classes from "./WorldStudioHeader.module.css";

const ROLES: readonly DocumentRole[] = ["world", "basis", "fragment", "shard"];

function downloadText(filename: string, text: string): void {
  const blob = new Blob([text], { type: "text/plain" });
  const url = URL.createObjectURL(blob);
  const anchor = window.document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

/**
 * The studio's masthead: the open document's name and role, where its source stands (checking, clean, or how many
 * problems), the official build and both engines (or a boot refusal by name), then the Open, Save draft, and
 * Download source actions. Bound entirely to `StudioContext` — no props.
 */
export function WorldStudioHeader() {
  const actor = StudioContext.useActorRef();
  const boot = useStudioBoot();
  const language = useStudioLanguage();
  const world = useStudioWorld();
  const workspace = useStudioWorkspace();
  const official = useStudioOfficial();
  const checking = useStudioChecking();
  const problems = useStudioProblems();
  const isDirty = useStudioIsDirty();
  const canSave = useStudioCanSaveDraft();
  const confirm = useStudioConfirmation();
  const [openModal, setOpenModal] = useState(false);
  const [saveDraftModal, setSaveDraftModal] = useState(false);
  const [draftTitle, setDraftTitle] = useState("");

  const bootLine = boot.status === "refused"
    ? `boot refused: ${boot.refusal}`
    : boot.build
      ? `${describeTree(boot.build)} · ${boot.build.source} · ${official?.manifest.channel ?? "?"} channel · language engine ${language.status}${language.refusal ? `: ${language.refusal}` : ""} · world engine ${world.status}${world.refusal ? `: ${world.refusal}` : ""}`
      : "booting…";
  const errors = problems.filter((problem) => problem.severity === "error").length;
  const status = !workspace ? { text: "no document", color: "gray" }
    : checking ? { text: "checking", color: "yellow" }
      : errors > 0 ? { text: `${errors} error${errors === 1 ? "" : "s"}`, color: "red" }
        : { text: "clean", color: "jade" };

  const replaceWorkspace = async (perform: () => void) => {
    if (!isDirty || await confirm("Discard unsaved source edits and open another document?")) perform();
  };
  const { drafts, error: draftError } = openModal ? readDraftListing(actor.getSnapshot().context.machineInput.draftStore) : { drafts: [], error: null };
  const documentsByRole = new Map<DocumentRole, ManifestDocumentEntry[]>();
  for (const entry of official?.manifest.documents ?? []) {
    documentsByRole.set(entry.role, [...(documentsByRole.get(entry.role) ?? []), entry]);
  }
  const title = workspace ? (workspace.draftId ? `${workspace.documentName} · ${workspace.draftId}` : workspace.documentName) : "No document";

  return (
    <Paper p="md">
      <Stack gap={4}>
        <Kicker>World Studio</Kicker>
        {/* Each masthead line keeps one line's height whatever it says, so nothing below it moves as it changes. */}
        <Group gap="sm" align="center" wrap="nowrap">
          <Title order={1} size="h2" className={classes.name} title={title}>{title}</Title>
          <Group gap={6} wrap="nowrap">
            <Badge color="gray">{workspace?.role ?? "—"}</Badge>
            <Badge color={status.color} role="status">{status.text}</Badge>
          </Group>
        </Group>
        <Text size="xs" c="dimmed" ff="monospace" truncate="end" title={bootLine}>
          {bootLine}
        </Text>
      </Stack>

      <Group gap="sm" justify="space-between" wrap="wrap" className={classes.toolbar}>
        <Text size="xs" c="dimmed" className={classes.label}>
          {workspace ? `${workspace.entry} · revision ${workspace.revision}` : "Open a document to edit its source."}
        </Text>
        <Group gap="xs" wrap="wrap">
          <Button size="xs" variant="default" leftSection={<RiFolderOpenLine size={16} />} disabled={boot.status !== "ready"} onClick={() => setOpenModal(true)}>
            Open
          </Button>
          <Button
            size="xs"
            variant="default"
            leftSection={<RiDownloadLine size={16} />}
            disabled={!workspace}
            onClick={() => workspace && downloadText(workspace.active.slice(workspace.active.lastIndexOf("/") + 1), workspace.files[workspace.active].text)}
          >
            Download source
          </Button>
          <Button
            size="xs"
            variant={isDirty ? "filled" : "default"}
            leftSection={<RiSaveLine size={16} />}
            disabled={!canSave}
            onClick={() => { setDraftTitle(workspace?.draftId ?? workspace?.documentName ?? ""); setSaveDraftModal(true); }}
          >
            Save draft
          </Button>
        </Group>
      </Group>

      <Modal opened={openModal} onClose={() => setOpenModal(false)} title="Open a document" size="lg" scrollAreaComponent={ScrollArea.Autosize}>
        <Stack gap="lg">
          {ROLES.map((role) => {
            const entries = documentsByRole.get(role) ?? [];
            if (entries.length === 0) return null;
            return (
              <Stack gap={6} key={role}>
                <Kicker>{role}</Kicker>
                <ul className={classes.list}>
                  {entries.map((entry) => (
                    <li key={entry.name}>
                      <UnstyledButton
                        className={classes.entry}
                        aria-current={entry.name === workspace?.documentName ? "true" : undefined}
                        onClick={() => void replaceWorkspace(() => { actor.send({ type: "OPEN_OFFICIAL", name: entry.name }); setOpenModal(false); })}
                      >
                        <Text size="sm" ff="monospace" className={classes.entryName}>{entry.name}</Text>
                        <Text size="xs" c="dimmed" ff="monospace" visibleFrom="xs">{entry.source}</Text>
                      </UnstyledButton>
                    </li>
                  ))}
                </ul>
              </Stack>
            );
          })}
          <Stack gap={6}>
            <Kicker>Local drafts</Kicker>
            {draftError && (
              <Alert color="red" icon={<RiErrorWarningLine size={18} />} title="Local drafts unreadable">
                {draftError}
              </Alert>
            )}
            {!draftError && drafts.length === 0 && (
              <EmptyState icon={<RiDraftLine size={22} />} title="No local drafts saved yet">
                Save draft keeps the files you changed in this browser.
              </EmptyState>
            )}
            {drafts.length > 0 && (
              <ul className={classes.list}>
                {drafts.map((draft) => (
                  <li key={draft.id}>
                    <UnstyledButton
                      className={classes.entry}
                      onClick={() => void replaceWorkspace(() => { actor.send({ type: "LOAD_DRAFT", id: draft.id }); setOpenModal(false); })}
                    >
                      <Text size="sm" className={classes.entryName}>{draft.title}</Text>
                      <Text size="xs" c="dimmed" ff="monospace" visibleFrom="xs">{draft.documentName}</Text>
                    </UnstyledButton>
                  </li>
                ))}
              </ul>
            )}
          </Stack>
        </Stack>
      </Modal>

      <Modal opened={saveDraftModal} onClose={() => setSaveDraftModal(false)} title="Save local draft">
        <Stack gap="sm">
          <TextInput data-autofocus label="Title" value={draftTitle} onChange={(e) => setDraftTitle(e.currentTarget.value)} />
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={() => setSaveDraftModal(false)}>Cancel</Button>
            <Button disabled={!canSave} onClick={() => { actor.send({ type: "SAVE_DRAFT", title: draftTitle || undefined }); setSaveDraftModal(false); }}>Save</Button>
          </Group>
        </Stack>
      </Modal>
    </Paper>
  );
}

export default WorldStudioHeader;
