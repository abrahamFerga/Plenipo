import { describe, expect, it } from "vitest";
import { parseAttachmentRefs, withAttachmentRefs } from "./attachments";
import type { StoredFileInfo } from "./api";

function stored(id: string, fileName: string): StoredFileInfo {
  return { id, fileName, contentType: "application/pdf", sizeBytes: 2048 };
}

describe("attachment refs", () => {
  it("round-trips: parseAttachmentRefs recovers what withAttachmentRefs appended", () => {
    const text = withAttachmentRefs("Please review these", [
      stored("f-1", "brief.pdf"),
      stored("f-2", "notes (v2).txt"),
    ]);

    expect(parseAttachmentRefs(text)).toEqual({
      body: "Please review these",
      files: [
        { id: "f-1", fileName: "brief.pdf" },
        { id: "f-2", fileName: "notes (v2).txt" },
      ],
    });
  });

  it("passes plain text (no attachment block) through untouched", () => {
    expect(withAttachmentRefs("Just a question", [])).toBe("Just a question");
    expect(parseAttachmentRefs("Just a question")).toEqual({
      body: "Just a question",
      files: [],
    });
  });

  it("recovers the default body when the user sent only attachments", () => {
    // withAttachmentRefs substitutes a stock body for an empty message, so the agent has a prompt.
    const text = withAttachmentRefs("", [stored("f-9", "brief.pdf")]);

    expect(parseAttachmentRefs(text)).toEqual({
      body: "Please look at the attached file(s).",
      files: [{ id: "f-9", fileName: "brief.pdf" }],
    });
  });
});
