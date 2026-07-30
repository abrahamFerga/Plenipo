import { afterEach, describe, expect, it, vi } from "vitest";
import {
  apiBase,
  clientConfig,
  configureClient,
  currencyForLocale,
  devAuthHeaders,
  normalizeApiBase,
  resetClientConfig,
} from "./config";
import { api } from "./api";

afterEach(() => {
  vi.unstubAllGlobals();
  resetClientConfig();
});

describe("normalizeApiBase", () => {
  it("defaults to localhost:8080 when nothing is configured", () => {
    expect(normalizeApiBase(undefined)).toBe("http://localhost:8080");
  });

  it("strips trailing slash(es) so request paths don't double up", () => {
    expect(normalizeApiBase("http://api.example.com/")).toBe("http://api.example.com");
    expect(normalizeApiBase("http://api.example.com//")).toBe("http://api.example.com");
  });

  it("leaves an already-clean base unchanged", () => {
    expect(normalizeApiBase("http://api.example.com")).toBe("http://api.example.com");
    expect(normalizeApiBase("http://localhost:8080")).toBe("http://localhost:8080");
  });
});

describe("configureClient", () => {
  it("normalizes the base it is given, so a configured trailing slash can't leak into URLs", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({}) } as unknown as Response);
    configureClient({ baseUrl: "https://api.example.com/", fetch: fetchMock });

    expect(apiBase()).toBe("https://api.example.com");
    await api.me();
    expect(fetchMock.mock.calls[0]?.[0]).toBe("https://api.example.com/api/platform/me");
  });

  it("routes requests through the configured transport, not the global fetch", async () => {
    const globalFetch = vi.fn();
    vi.stubGlobal("fetch", globalFetch);
    const configured = vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({}) } as unknown as Response);
    configureClient({ fetch: configured });

    await api.me();

    expect(configured).toHaveBeenCalledOnce();
    expect(globalFetch).not.toHaveBeenCalled();
  });

  it("asks for auth headers per request, so a refreshed token is picked up mid-session", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({}) } as unknown as Response);
    let token = "first";
    configureClient({ fetch: fetchMock, authHeaders: () => ({ Authorization: `Bearer ${token}` }) });

    await api.me();
    token = "second";
    await api.me();

    const header = (i: number) =>
      (fetchMock.mock.calls[i]?.[1] as RequestInit & { headers: Record<string, string> }).headers.Authorization;
    expect(header(0)).toBe("Bearer first");
    expect(header(1)).toBe("Bearer second");
  });

  it("awaits async auth headers (a mobile client reads its token from secure storage)", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({}) } as unknown as Response);
    configureClient({
      fetch: fetchMock,
      authHeaders: async () => ({ Authorization: "Bearer stored" }),
    });

    await api.me();

    const headers = (fetchMock.mock.calls[0]?.[1] as RequestInit & { headers: Record<string, string> }).headers;
    expect(headers.Authorization).toBe("Bearer stored");
  });

  it("sends dev-auth headers until a shell configures real credentials", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({}) } as unknown as Response);
    configureClient({ fetch: fetchMock });

    await api.me();

    const headers = (fetchMock.mock.calls[0]?.[1] as RequestInit & { headers: Record<string, string> }).headers;
    expect(headers["X-Dev-Subject"]).toBe(devAuthHeaders["X-Dev-Subject"]);
  });

  it("merges field-default sources over the built-ins instead of replacing them", () => {
    configureClient({ fieldDefaultSources: { "browser-currency": () => "MXN" } });
    const sources = clientConfig().fieldDefaultSources;

    expect(sources["browser-currency"]?.()).toBe("MXN");
    // The one it didn't mention still resolves — a shell improves one source without restating the rest.
    expect(typeof sources["browser-timezone"]).toBe("function");
  });

  it("keeps unspecified keys on a second, partial call", () => {
    const fetchMock = vi.fn();
    configureClient({ baseUrl: "https://a.example.com", fetch: fetchMock });
    configureClient({ authHeaders: () => ({ A: "1" }) });

    expect(apiBase()).toBe("https://a.example.com");
    expect(clientConfig().fetch).toBe(fetchMock);
  });
});

describe("currencyForLocale", () => {
  it("reads the region off a region-tagged locale", () => {
    expect(currencyForLocale("es-MX")).toBe("MXN");
  });

  it("fills in the likely region when the tag omits it", () => {
    expect(currencyForLocale("en")).toBe("USD");
  });

  it("maps a eurozone locale to EUR", () => {
    expect(currencyForLocale("de-DE")).toBe("EUR");
  });

  it("offers no guess for a region the map doesn't cover, rather than a wrong one", () => {
    expect(currencyForLocale("es-BO")).toBeUndefined();
  });

  it("offers no guess for nothing, or for nonsense", () => {
    expect(currencyForLocale(undefined)).toBeUndefined();
    expect(currencyForLocale("not a locale")).toBeUndefined();
  });
});
