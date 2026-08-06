// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { LocalUsersPanel } from "./LocalUsersPanel";

const USERS = [
  {
    userId: "u-1",
    email: "ada@example.com",
    displayName: "Ada",
    isActive: true,
    mustChangePassword: true,
    lockedUntil: "2099-01-01T00:00:00Z",
    totpEnabled: true,
    lastSignInAt: null,
  },
];

function stubApi(local: boolean) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    if (url.includes("/api/platform/auth-config")) {
      return Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve({ mode: "oidc", authority: "http://localhost", clientId: "plenipo-web", scopes: null, local }),
      } as unknown as Response);
    }
    if (url.includes("/api/admin/users/local") && method === "GET") {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(USERS) } as unknown as Response);
    }
    if (url.includes("/api/admin/users/local") && method === "POST") {
      return Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve({
            userId: "u-2",
            email: "grace@example.com",
            temporaryPassword: "abcd-efgh-jkmn-pqrs",
            message: "Share the temporary password with grace@example.com securely.",
          }),
      } as unknown as Response);
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve(null) } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <LocalUsersPanel allRoles={["tenant_admin", "user"]} />
    </QueryClientProvider>,
  );
}

describe("LocalUsersPanel", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  it("renders nothing on an external-IdP deployment", async () => {
    const fetchMock = stubApi(false);
    renderPanel();

    // Wait for auth-config to resolve, then assert the panel stayed out of the tree — and that it
    // never even asked for the credential list it wouldn't show.
    await waitFor(() =>
      expect(fetchMock.mock.calls.some((c) => String(c[0]).includes("/auth-config"))).toBe(true));
    expect(screen.queryByText("Local sign-in accounts")).toBeNull();
    expect(fetchMock.mock.calls.some((c) => String(c[0]).includes("/api/admin/users/local"))).toBe(false);
  });

  it("lists accounts with their lockout / forced-change / MFA state", async () => {
    stubApi(true);
    renderPanel();

    expect(await screen.findByText("Ada")).toBeTruthy();
    expect(screen.getByText("locked")).toBeTruthy();
    expect(screen.getByText("must change password")).toBeTruthy();
    expect(screen.getByText("MFA")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Unlock" })).toBeTruthy();
  });

  it("creates an account and reveals the temporary password exactly once", async () => {
    const fetchMock = stubApi(true);
    renderPanel();
    await screen.findByText("Ada");

    fireEvent.change(screen.getByPlaceholderText("ada@example.com"), {
      target: { value: "grace@example.com" },
    });
    fireEvent.click(screen.getByLabelText(/tenant_admin/));
    fireEvent.click(screen.getByRole("button", { name: "Create account" }));

    await waitFor(() => {
      const post = fetchMock.mock.calls.find(
        (c) => String(c[0]).includes("/api/admin/users/local") && (c[1] as RequestInit)?.method === "POST",
      );
      expect(post).toBeTruthy();
      expect(JSON.parse((post![1] as RequestInit).body as string)).toEqual({
        email: "grace@example.com",
        displayName: null,
        roles: ["tenant_admin"],
      });
    });

    // The password is IN the page now — and nowhere else, ever again.
    expect(await screen.findByText("abcd-efgh-jkmn-pqrs")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(screen.queryByText("abcd-efgh-jkmn-pqrs")).toBeNull();
  });
});
