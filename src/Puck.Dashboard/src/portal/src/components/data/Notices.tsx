import { Alert, Button, CopyButton, Group, Text, TextInput } from "@mantine/core";
import {
  RiCheckboxCircleLine,
  RiCheckLine,
  RiDownload2Line,
  RiErrorWarningLine,
  RiFileCopyLine,
  RiInboxArchiveLine,
  RiTerminalBoxLine,
} from "@remixicon/react";
import classes from "./Notices.module.css";
import { describeShareUri, NOT_A_SHARE_LINK, readFunctionFor, type ShareNotice } from "./storage";

/** A failed file action, named, until the user dismisses it. */
export function ActionError({ message, onClose }: { message: string; onClose: () => void }) {
  return (
    <Alert
      color="red"
      icon={<RiErrorWarningLine size={18} />}
      onClose={onClose}
      title="Something went wrong"
      variant="light"
      withCloseButton
    >
      {message}
    </Alert>
  );
}

/** What a share, publish, or unpublish produced: the address to hand on, when there is one, and its terms. */
export function ShareNoticeAlert({ notice, onClose }: { notice: ShareNotice; onClose: () => void }) {
  return (
    <Alert
      color="jade"
      icon={<RiCheckboxCircleLine size={18} />}
      onClose={onClose}
      title={notice.title}
      variant="light"
      withCloseButton
    >
      {notice.uri ? (
        <Group align="flex-end" gap="xs" wrap="nowrap">
          <TextInput
            aria-label="Link"
            classNames={{ input: classes.link }}
            flex={1}
            onFocus={(event) => event.currentTarget.select()}
            readOnly
            size="sm"
            value={notice.uri}
          />
          <CopyButton value={notice.uri}>
            {({ copied, copy }) => (
              <Button
                color={copied ? "jade" : undefined}
                leftSection={copied ? <RiCheckLine size={16} /> : <RiFileCopyLine size={16} />}
                onClick={copy}
                size="sm"
                variant="default"
              >
                {copied ? "Copied" : "Copy link"}
              </Button>
            )}
          </CopyButton>
        </Group>
      ) : null}
      <Text c="dimmed" mt={notice.uri ? "xs" : 0} size="xs">
        {notice.note}
      </Text>
    </Alert>
  );
}

interface IncomingShareProps {
  isDownloading: boolean;
  isRunning: boolean;
  onClose: () => void;
  onDownload: () => void;
  onQuery: (readFunction: string) => void;
  uri: string;
}

/** A file someone shared with this user, opened from a share link: query it here, or download it. */
export function IncomingShare({ isDownloading, isRunning, onClose, onDownload, onQuery, uri }: IncomingShareProps) {
  const described = describeShareUri(uri);
  const readFunction = readFunctionFor(described.name);

  if (!described.isByteTerrace) {
    return (
      <Alert color="red" icon={<RiInboxArchiveLine size={18} />} onClose={onClose} title="This share link cannot be opened" variant="light" withCloseButton>
        {NOT_A_SHARE_LINK}
      </Alert>
    );
  }

  return (
    <Alert
      icon={<RiInboxArchiveLine size={18} />}
      onClose={onClose}
      title={`A file was shared with you: ${described.name}`}
      variant="light"
      withCloseButton
    >
      <Group gap="xs">
        {readFunction ? (
          <Button
            leftSection={<RiTerminalBoxLine size={16} />}
            loading={isRunning}
            onClick={() => onQuery(readFunction)}
            size="compact-sm"
            variant="light"
          >
            Query
          </Button>
        ) : null}
        <Button
          leftSection={<RiDownload2Line size={16} />}
          loading={isDownloading}
          onClick={onDownload}
          size="compact-sm"
          variant="default"
        >
          Download
        </Button>
        {described.expiresOn ? (
          <Text c="dimmed" ff="monospace" size="xs">
            Expires {new Date(described.expiresOn).toLocaleString()}
          </Text>
        ) : null}
      </Group>
    </Alert>
  );
}
