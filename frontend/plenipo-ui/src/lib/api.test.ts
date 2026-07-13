import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, api } from "./api";

// devAuth resolves API_BASE from import.meta.env.VITE_API_BASE, which is unset under test → the default.
const BASE = "http://localhost:8080";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("api client", () => {
  it("prefixes the API base and returns the parsed JSON", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: () => Promise.resolve([{ id: "finance" }]),
    } as unknown as Response);
    vi.stubGlobal("fetch", fetchMock);

    const modules = await api.modules();

    expect(modules).toEqual([{ id: "finance" }]);
    expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/api/platform/modules`);
  });

  it("passes query parameters to admin reports", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({}),
    } as unknown as Response);
    vi.stubGlobal("fetch", fetchMock);

    await api.admin.usage(7);

    expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/api/admin/usage?days=7`);
  });

  it("throws including the status code on a non-2xx response", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: false, status: 404, statusText: "Not Found" } as unknown as Response),
    );

    await expect(api.me()).rejects.toThrow("404");
  });

  it("throws a typed ApiError carrying the HTTP status so callers can branch on it", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: false, status: 403, statusText: "Forbidden" } as unknown as Response),
    );

    const error = await api.me().catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(403);
  });

  it("captures the server's problem-details body and folds its detail into the message", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: false,
        status: 400,
        statusText: "Bad Request",
        text: () => Promise.resolve(JSON.stringify({ detail: "Name is required." })),
      } as unknown as Response),
    );

    const error = (await api.me().catch((e: unknown) => e)) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(400);
    expect(error.body).toContain("Name is required.");
    expect(error.message).toContain("Name is required.");
  });

  it("folds a bare JSON string error body into the message (ASP.NET Core Results.BadRequest(string))", async () => {
    // Minimal APIs serialize a string result as JSON, so the body arrives quoted: "…". Most of the platform's
    // admin validation errors take this shape; the detail must still reach the caller, not just "400".
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({
        ok: false,
        status: 400,
        statusText: "Bad Request",
        text: () => Promise.resolve(JSON.stringify("Unknown permission(s): tools.zzz.*.")),
      } as unknown as Response),
    );

    const error = (await api.me().catch((e: unknown) => e)) as ApiError;

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(400);
    expect(error.message).toContain("Unknown permission(s): tools.zzz.*.");
  });

  it("url-encodes a role name when revoking", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true } as unknown as Response);
    vi.stubGlobal("fetch", fetchMock);

    await api.admin.revokeRole("u-1", "tenant admin");

    expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/api/admin/users/u-1/roles/tenant%20admin`);
    expect(fetchMock.mock.calls[0]?.[1]?.method).toBe("DELETE");
  });
});
