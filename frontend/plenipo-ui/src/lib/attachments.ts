import type { StoredFileInfo } from "./api";

/**
 * Appends attachment references to the outgoing message. The convention is plain text on purpose:
 * it survives every channel (web, AG-UI, SignalR, WhatsApp) and the agent's document tools take the
 * file id directly (read_document, ocr_document).
 */
export function withAttachmentRefs(text: string, attachments: StoredFileInfo[]): string {
  if (attachments.length === 0) return text;
  const refs = attachments.map((a) => `- ${a.fileName} (file id: ${a.id})`).join("\n");
  const body = text || "Please look at the attached file(s).";
  return `${body}\n\n[Attached files]\n${refs}`;
}

/** A file reference recovered from a message's trailing "[Attached files]" block. */
export interface AttachmentRef {
  id: string;
  fileName: string;
}

/**
 * Parses the trailing block {@link withAttachmentRefs} produces back into the message body and its
 * file references, so the chat history can render attachments as chips instead of raw text. Text
 * without a well-formed trailing block passes through untouched (body = the whole text, no files).
 */
export function parseAttachmentRefs(text: string): { body: string; files: AttachmentRef[] } {
  const block = /\n\n\[Attached files\]\n((?:- .+ \(file id: [^)]+\)\n?)+)$/.exec(text);
  if (!block) {
    return { body: text, files: [] };
  }
  const files: AttachmentRef[] = [];
  for (const line of block[1].split("\n")) {
    const ref = /^- (.+) \(file id: ([^)]+)\)$/.exec(line);
    if (ref) {
      files.push({ fileName: ref[1], id: ref[2] });
    }
  }
  return { body: text.slice(0, block.index), files };
}
