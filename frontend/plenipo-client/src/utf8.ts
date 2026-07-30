/**
 * Streaming UTF-8 decoding for the AG-UI chat transport.
 *
 * Browsers have `TextDecoder` and we use it. React Native does not guarantee one — Hermes ships
 * without it and whether a polyfill is installed depends on the Expo SDK and the app's entry
 * file — and the chat stream is the one place a missing global would surface as a blank assistant
 * reply rather than a clean error. So the client carries its own decoder for that case.
 *
 * The hard part of decoding a *stream* is that a chunk boundary can land mid-character: a two-byte
 * "é" arriving as one byte now and one byte next. Both paths hold an incomplete trailing sequence
 * back until its remaining bytes arrive.
 */

const EMPTY = new Uint8Array(0);

function concat(a: Uint8Array, b: Uint8Array): Uint8Array {
  const out = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}

/** Bytes in a sequence, from its lead byte. Invalid lead bytes report 1 so decoding can advance. */
function sequenceLength(lead: number): number {
  if (lead < 0x80) return 1;
  if ((lead & 0xe0) === 0xc0) return 2;
  if ((lead & 0xf0) === 0xe0) return 3;
  if ((lead & 0xf8) === 0xf0) return 4;
  return 1;
}

/**
 * How many bytes of `bytes` end on a character boundary — i.e. everything except a trailing
 * sequence that is still missing bytes. Walks back over at most three continuation bytes
 * (`10xxxxxx`) to find the lead byte they belong to.
 */
export function completeLength(bytes: Uint8Array): number {
  let i = bytes.length - 1;
  let back = 0;
  while (i >= 0 && (bytes[i] & 0xc0) === 0x80 && back < 3) {
    i--;
    back++;
  }
  // No lead byte in reach — malformed input, not a split character. Decode it and let the
  // replacement characters speak.
  if (i < 0) return bytes.length;
  return i + sequenceLength(bytes[i]) <= bytes.length ? bytes.length : i;
}

/** Decodes `bytes[start, end)` as UTF-8, substituting U+FFFD for malformed sequences. */
export function decodeUtf8(bytes: Uint8Array, start = 0, end = bytes.length): string {
  let out = "";
  let i = start;
  while (i < end) {
    const lead = bytes[i];
    const size = sequenceLength(lead);
    if (lead >= 0x80 && size === 1) {
      // A continuation or invalid byte where a lead byte belongs.
      out += "�";
      i += 1;
      continue;
    }
    if (i + size > end) {
      out += "�";
      break;
    }

    let cp = size === 1 ? lead : lead & (0xff >> (size + 1));
    let valid = true;
    for (let k = 1; k < size; k++) {
      const b = bytes[i + k];
      if ((b & 0xc0) !== 0x80) {
        valid = false;
        break;
      }
      cp = (cp << 6) | (b & 0x3f);
    }
    i += size;

    if (!valid) {
      out += "�";
    } else if (cp > 0xffff) {
      const astral = cp - 0x10000;
      out += String.fromCharCode(0xd800 + (astral >> 10), 0xdc00 + (astral & 0x3ff));
    } else {
      out += String.fromCharCode(cp);
    }
  }
  return out;
}

/**
 * A stateful `(bytes, stream) => string` decoder: `TextDecoder` when the runtime has one,
 * otherwise the built-in fallback. Call with `stream: true` for every chunk but the last.
 */
export function createStreamingDecoder(): (bytes: Uint8Array, stream: boolean) => string {
  if (typeof TextDecoder !== "undefined") {
    const decoder = new TextDecoder();
    return (bytes, stream) => decoder.decode(bytes, { stream });
  }

  let carry = EMPTY;
  return (bytes, stream) => {
    const buffer = carry.length === 0 ? bytes : concat(carry, bytes);
    const cut = stream ? completeLength(buffer) : buffer.length;
    carry = cut < buffer.length ? buffer.slice(cut) : EMPTY;
    return decodeUtf8(buffer, 0, cut);
  };
}
