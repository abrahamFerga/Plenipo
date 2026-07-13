import { describe, expect, it } from "vitest";
import { messageId, parseAguiFrames } from "./agui";

describe("parseAguiFrames", () => {
  it("parses complete frames and keeps a partial frame as the tail", () => {
    const { events, rest } = parseAguiFrames(
      'data: {"type":"RUN_STARTED"}\n\n' +
        'data: {"type":"TEXT_MESSAGE_CONTENT","delta":"hi"}\n\n' +
        'data: {"type":"RUN_FIN',
    );

    expect(events.map((e) => e.type)).toEqual(["RUN_STARTED", "TEXT_MESSAGE_CONTENT"]);
    expect(events[1].delta).toBe("hi");
    expect(rest).toBe('data: {"type":"RUN_FIN'); // the incomplete frame is retained for the next chunk
  });

  it("skips frames with no data line (e.g. SSE keep-alive comments)", () => {
    const { events, rest } = parseAguiFrames(": keep-alive\n\n");
    expect(events).toEqual([]);
    expect(rest).toBe("");
  });

  it("returns the whole buffer as the tail when no frame is complete yet", () => {
    const { events, rest } = parseAguiFrames('data: {"type":"RUN_STARTED"}');
    expect(events).toEqual([]);
    expect(rest).toBe('data: {"type":"RUN_STARTED"}');
  });

  it("accepts CRLF line endings (SSE from any server, not just LF)", () => {
    const { events, rest } = parseAguiFrames(
      'data: {"type":"RUN_STARTED"}\r\n\r\ndata: {"type":"RUN_FINISHED"}\r\n\r\n',
    );
    expect(events.map((e) => e.type)).toEqual(["RUN_STARTED", "RUN_FINISHED"]);
    expect(rest).toBe("");
  });

  it("skips a malformed frame instead of aborting the stream", () => {
    const { events } = parseAguiFrames(
      "data: not-json\n\n" + 'data: {"type":"RUN_FINISHED"}\n\n',
    );
    // The bad frame is dropped; the following valid event still comes through.
    expect(events.map((e) => e.type)).toEqual(["RUN_FINISHED"]);
  });
});

describe("messageId", () => {
  it("returns a non-empty id in a normal (secure) context", () => {
    expect(messageId()).toBeTruthy();
  });

  it("falls back to a simple unique id when crypto.randomUUID is unavailable (insecure context)", () => {
    const original = globalThis.crypto.randomUUID;
    Object.defineProperty(globalThis.crypto, "randomUUID", { value: undefined, configurable: true });
    try {
      const id = messageId();
      expect(id).toBeTruthy();
      expect(id.length).toBeGreaterThan(0);
    } finally {
      Object.defineProperty(globalThis.crypto, "randomUUID", { value: original, configurable: true });
    }
  });
});
