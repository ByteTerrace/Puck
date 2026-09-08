import { useEffect, useMemo, useState } from "react";
import { Badge, Button, FileButton, Group, Modal, Paper, Stack, Text, Textarea, TextInput } from "@mantine/core";
import {
  RiArrowLeftLine,
  RiArrowRightLine,
  RiDownloadLine,
  RiFolderOpenLine,
  RiSaveLine,
  RiUploadLine,
} from "@remixicon/react";
import {
  StudioContext,
  useStudioBoot,
  useStudioCanRedo,
  useStudioCanUndo,
  useStudioDocument,
  useStudioOfficial,
  useStudioIsDirty,
} from "../../context/StudioContext";
import type { DocumentRole } from "../../document/documentRole";
import { checkDocument } from "../../document/intake";
import { readDraftListing } from "../../document/localDrafts";
import type { ManifestDocumentEntry } from "../../official/manifest";
import { useStudioConfirmation } from "./StudioConfirmation";

const ROLE_LABEL: Record<DocumentRole, string> = {
  world: "world",
  basis: "basis",
  fragment: "fragment",
  shard: "shard",
};

const VALIDATION_LABEL: Record<string, { text: string; color: string }> = {
  pending: { text: "pending", color: "yellow" },
  clean: { text: "clean", color: "teal" },
  refused: { text: "refused", color: "red" },
};

function isTextEditingTarget(target: EventTarget | null): boolean {
  return (target instanceof HTMLElement) && !!target.closest('input, textarea, select, [contenteditable="true"], [role="dialog"]');
}

function downloadText(filename: string, text: string): void {
  const blob = new Blob([text], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const anchor = window.document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

/**
 * The studio's document toolbar: the official build (or the boot refusal by name), the open
 * document's name and role, validation status, undo/redo, and the Open/Import/Export/Save-draft
 * document actions. Bound entirely to `StudioContext` — no props.
 */
export function WorldStudioHeader() {
  const actor = StudioContext.useActorRef();
  const boot = useStudioBoot();
  const document = useStudioDocument();
  const official = useStudioOfficial();
  const canUndo = useStudioCanUndo();
  const canRedo = useStudioCanRedo();
  const isDirty = useStudioIsDirty();
  const canSave = StudioContext.useSelector(snapshot => snapshot.matches({ ready: { document: "idle" } }));
  const confirm = useStudioConfirmation();
  const replaceDocument = async (perform: () => void) => {
    if (!isDirty || await confirm("Discard unsaved edits and replace this document?")) perform();
  };

  const [openModal, setOpenModal] = useState(false);
  const [importModal, setImportModal] = useState(false);
  const [importText, setImportText] = useState("");
  const [importError, setImportError] = useState<string | null>(null);
  const [saveDraftModal, setSaveDraftModal] = useState(false);
  const [draftTitle, setDraftTitle] = useState("");

  useEffect(() => {
    const shortcut = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey || isTextEditingTarget(event.target)) return;
      const key = event.key.toLowerCase();
      if (key !== "z" && key !== "y") return;
      event.preventDefault();
      if (key === "y" || event.shiftKey) {
        if (canRedo) actor.send({ type: "REDO" });
      } else if (canUndo) {
        actor.send({ type: "UNDO" });
      }
    };
    window.addEventListener("keydown", shortcut);
    return () => window.removeEventListener("keydown", shortcut);
  }, [actor, canUndo, canRedo]);

  const validation = VALIDATION_LABEL[document.validation] ?? VALIDATION_LABEL.pending;
  const draftStore = actor.getSnapshot().context.machineInput.draftStore;
  const { drafts, error: draftError } = useMemo(() => openModal ? readDraftListing(draftStore) : { drafts: [], error: null }, [openModal, draftStore]);
  const documentsByRole = new Map<DocumentRole, ManifestDocumentEntry[]>();
  if (official) {
    for (const entry of official.manifest.documents) {
      const list = documentsByRole.get(entry.role) ?? [];
      list.push(entry);
      documentsByRole.set(entry.role, list);
    }
  }

  const openImport = () => { setImportText(document.text); setImportError(null); setImportModal(true); };
  const applyImport = () => {
    // `checkDocument` runs the same byte-size intake check the machine's own `OPEN_TEXT` edit
    // reducer runs (`openTextDocument`, `document/intake.ts`) — checked again here so the modal
    // can show the refusal by name inline instead of only via `WorldStudioAlerts` a beat later.
    try {
      checkDocument(importText);
    } catch (error) {
      setImportError((error as Error).message);
      return;
    }
    void replaceDocument(() => {
      actor.send({ type: "OPEN_TEXT", text: importText });
      setImportModal(false);
    });
  };
  const importFile = (file: File | null) => {
    if (!file) return;
    void file.text().then((text) => { setImportText(text); setImportError(null); });
  };

  return (
    <Paper p="sm" radius="md" withBorder className="studio-document-toolbar">
      <Group justify="space-between" gap="sm" wrap="wrap">
        <div>
          <Text className="kicker">Puck authoring studio</Text>
          <Group gap="xs" align="baseline">
            <Text component="h1" m={0} fw={600} size="lg">{document.name}</Text>
            <Badge size="xs" variant="light">{ROLE_LABEL[document.role]}</Badge>
            <Badge size="xs" variant="light" color={validation.color} role="status">
              {validation.text}{document.diagnostics.length > 0 ? ` (${document.diagnostics.length})` : ""}
            </Badge>
          </Group>
          <Text size="xs" c="dimmed" mt={2}>
            {boot.status === "refused"
              ? `boot refused: ${boot.refusal}`
              : boot.build
                ? `commit ${boot.build.commit.slice(0, 12)} · ${boot.build.source} · ${official?.manifest.channel ?? "?"} channel`
                : "booting…"}
          </Text>
        </div>
        <Group gap="xs">
          <Button size="xs" variant="default" leftSection={<RiFolderOpenLine size={15} />} disabled={boot.status !== "ready"} onClick={() => setOpenModal(true)}>Open</Button>
          <Button size="xs" variant="default" leftSection={<RiUploadLine size={15} />} onClick={openImport}>Import JSON</Button>
          <Button size="xs" variant="default" leftSection={<RiDownloadLine size={15} />} onClick={() => downloadText(`${document.name}.json`, document.text)}>Export JSON</Button>
          <Button size="xs" variant="default" leftSection={<RiSaveLine size={15} />} onClick={() => { setDraftTitle(document.name); setSaveDraftModal(true); }}>Save draft</Button>
        </Group>
      </Group>
      <Group gap="xs" mt="sm">
        <Button size="compact-xs" variant="default" leftSection={<RiArrowLeftLine size={14} />} disabled={!canUndo} onClick={() => actor.send({ type: "UNDO" })}>Undo</Button>
        <Button size="compact-xs" variant="default" rightSection={<RiArrowRightLine size={14} />} disabled={!canRedo} onClick={() => actor.send({ type: "REDO" })}>Redo</Button>
        <Text size="xs" c="dimmed" role="status" style={{ flex: 1 }}>{document.label}</Text>
      </Group>

      <Modal opened={openModal} onClose={() => setOpenModal(false)} title="Open a document" size="lg">
        <Stack gap="md">
          {(["world", "basis", "fragment", "shard"] as const).map((role) => {
            const entries = documentsByRole.get(role) ?? [];
            if (entries.length === 0) return null;
            return (
              <div key={role}>
                <Text size="xs" c="dimmed" mb={4} tt="uppercase">{role}</Text>
                <Stack gap={4}>
                  {entries.map((entry) => (
                    <Button
                      key={entry.name}
                      variant="default"
                      size="xs"
                      justify="flex-start"
                      onClick={() => void replaceDocument(() => { actor.send({ type: "OPEN_OFFICIAL", name: entry.name }); setOpenModal(false); })}
                    >
                      {entry.name}
                    </Button>
                  ))}
                </Stack>
              </div>
            );
          })}
          <div>
            <Text size="xs" c="dimmed" mb={4} tt="uppercase">Local drafts</Text>
            {draftError && <Text size="sm" c="red" role="alert">{draftError}</Text>}
            {!draftError && drafts.length === 0 && <Text size="xs" c="dimmed">No local drafts saved yet.</Text>}
            <Stack gap={4}>
              {drafts.map((draft) => (
                <Button
                  key={draft.id}
                  variant="default"
                  size="xs"
                  justify="flex-start"
                  onClick={() => void replaceDocument(() => { actor.send({ type: "LOAD_DRAFT", id: draft.id }); setOpenModal(false); })}
                >
                  {draft.title}
                </Button>
              ))}
            </Stack>
          </div>
        </Stack>
      </Modal>

      <Modal opened={importModal} onClose={() => setImportModal(false)} title="Import JSON" size="lg">
        <Stack gap="sm">
          <FileButton onChange={importFile} accept="application/json,.json">
            {(props) => <Button {...props} size="xs" variant="default">Choose a file…</Button>}
          </FileButton>
          <Textarea autosize minRows={8} maxRows={20} value={importText} onChange={(e) => setImportText(e.currentTarget.value)} styles={{ input: { fontFamily: "ui-monospace,monospace" } }} />
          {importError && <Text size="xs" c="red" role="alert">{importError}</Text>}
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setImportModal(false)}>Cancel</Button>
            <Button onClick={applyImport}>Open as new document</Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={saveDraftModal} onClose={() => setSaveDraftModal(false)} title="Save local draft">
        <Stack gap="sm">
          <TextInput label="Title" value={draftTitle} onChange={(e) => setDraftTitle(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setSaveDraftModal(false)}>Cancel</Button>
            <Button disabled={!canSave} onClick={() => { actor.send({ type: "SAVE_DRAFT", title: draftTitle || undefined }); setSaveDraftModal(false); }}>Save</Button>
          </Group>
        </Stack>
      </Modal>
    </Paper>
  );
}

export default WorldStudioHeader;
