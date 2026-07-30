import { QueryClient } from "@tanstack/react-query";
import { configureClient, resetClientConfig } from "@plenipo/client";

/**
 * A fake Plenipo API for the shell's tests.
 *
 * The shell is a projection of what the server sends, so its tests are about that mapping and
 * nothing else — no network, no backend, no device. Register the responses a scenario needs and
 * assert on what got rendered and what got posted.
 */

export interface RecordedCall {
  url: string;
  method: string;
  body?: unknown;
}

export interface FakeApi {
  /** Register a handler, keyed by path ("/api/x") or method + path ("POST /api/x"). */
  on(key: string, handler: (path: string) => unknown): void;
  /** Every request the shell made, in order. */
  calls: RecordedCall[];
  /** Point @plenipo/client at this fake. Call in beforeEach. */
  install(): void;
  /** Restore the client defaults. Call in afterEach. */
  reset(): void;
}

const ORIGIN = "http://api.test";

export function fakeApi(): FakeApi {
  const routes = new Map<string, (path: string) => unknown>();
  const calls: RecordedCall[] = [];

  const transport = (url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    const path = url.replace(ORIGIN, "");
    calls.push({
      url: path,
      method,
      body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
    });

    const handler = routes.get(`${method} ${path}`) ?? routes.get(path);
    if (handler === undefined) {
      // An unregistered route is a 404, not a hang — a test that forgot a response should fail
      // with the shell's own error path rather than time out.
      return Promise.resolve({
        ok: false,
        status: 404,
        statusText: "Not Found",
        text: () => Promise.resolve(""),
      } as unknown as Response);
    }
    return Promise.resolve({
      ok: true,
      status: 200,
      json: () => Promise.resolve(handler(path)),
    } as unknown as Response);
  };

  return {
    calls,
    on: (key, handler) => routes.set(key, handler),
    install: () =>
      configureClient({ baseUrl: ORIGIN, authHeaders: () => ({}), fetch: transport }),
    reset: () => {
      routes.clear();
      calls.length = 0;
      resetClientConfig();
    },
  };
}

/**
 * A query client for a test: no retries (a 404 should surface immediately, not after three
 * attempts) and no cache-eviction timer left running past the test.
 */
export function testQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
}
