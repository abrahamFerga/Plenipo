import { afterEach, describe, expect, it, vi } from "vitest";
import { completeLength, createStreamingDecoder, decodeUtf8 } from "./utf8";

const bytes = (...values: number[]) => new Uint8Array(values);
const encode = (text: string) => new TextEncoder().encode(text);

afterEach(() => vi.unstubAllGlobals());

describe("decodeUtf8", () => {
  it("decodes the four sequence widths", () => {
    expect(decodeUtf8(encode("plain ascii"))).toBe("plain ascii");
    expect(decodeUtf8(encode("café"))).toBe("café"); // 2-byte
    expect(decodeUtf8(encode("€ 100"))).toBe("€ 100"); // 3-byte
    expect(decodeUtf8(encode("🔒 secured"))).toBe("🔒 secured"); // 4-byte → surrogate pair
  });

  it("substitutes U+FFFD for a stray continuation byte instead of throwing", () => {
    expect(decodeUtf8(bytes(0x41, 0x80, 0x42))).toBe("A�B");
  });

  it("substitutes U+FFFD for a truncated sequence", () => {
    expect(decodeUtf8(bytes(0x41, 0xe2, 0x82))).toBe("A�");
  });
});

describe("completeLength", () => {
  it("returns the whole buffer when it ends on a character boundary", () => {
    const whole = encode("café");
    expect(completeLength(whole)).toBe(whole.length);
  });

  it("holds back a trailing sequence that is still missing bytes", () => {
    const euro = encode("€"); // 3 bytes
    expect(completeLength(euro.slice(0, 2))).toBe(0);
    expect(completeLength(euro.slice(0, 1))).toBe(0);
  });

  it("holds back only the incomplete tail, not the text before it", () => {
    const buffer = encode("hi€");
    expect(completeLength(buffer.slice(0, 4))).toBe(2); // "hi" complete, € half-arrived
  });
});

describe("createStreamingDecoder", () => {
  /** The property that matters: a chunk boundary landing mid-character must not corrupt the text. */
  function decodesSplitChunks(decode: (b: Uint8Array, stream: boolean) => string) {
    const all = encode("café €5 🔒");
    let out = "";
    // One byte at a time is the worst case, and splits every multi-byte sequence there is.
    for (let i = 0; i < all.length; i++) out += decode(all.slice(i, i + 1), true);
    out += decode(new Uint8Array(0), false);
    return out;
  }

  it("reassembles characters split across chunks (TextDecoder path)", () => {
    expect(typeof TextDecoder).toBe("function");
    expect(decodesSplitChunks(createStreamingDecoder())).toBe("café €5 🔒");
  });

  it("reassembles characters split across chunks (fallback path, no TextDecoder)", () => {
    vi.stubGlobal("TextDecoder", undefined);
    expect(decodesSplitChunks(createStreamingDecoder())).toBe("café €5 🔒");
  });

  it("fallback matches TextDecoder on a realistic SSE frame", () => {
    const frame = 'data: {"type":"TEXT_MESSAGE_CONTENT","delta":"Gastaste 1 200 € — ¡cuidado! 🔒"}\n\n';
    const encoded = encode(frame);

    const withDecoder = createStreamingDecoder()(encoded, false);
    vi.stubGlobal("TextDecoder", undefined);
    const withFallback = createStreamingDecoder()(encoded, false);

    expect(withFallback).toBe(withDecoder);
    expect(withFallback).toBe(frame);
  });
});
