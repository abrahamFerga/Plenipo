// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { KnowledgeAdmin } from "./KnowledgeAdmin";

const COLLECTIONS = [
  {
    id: "11111111-1111-1111-1111-111111111111",
    moduleId: "knowledge",
    resourceType: null,
    resourceId: null,
    name: "ES employment law",
    language: "spanish",
    embeddingModel: "mock-bow-384",
    documentCount: 3,
    chunkCount: 42,
    metadata: { jurisdiction: "ES" },
    filterKeys: ["effectiveYear", "jurisdiction"],
    isEditable: true,
  },
  {
    id: "22222222-2222-2222-2222-222222222222",
    moduleId: "legal",
    resourceType: "matter",
    resourceId: "33333333-3333-3333-3333-333333333333",
    name: "matter: Acme diligence",
    language: "english",
    embeddingModel: "mock-bow-384",
    documentCount: 1,
    chunkCount: 7,
    metadata: {},
    filterKeys: [],
    isEditable: false,
  },
];

function stubApi(overrides: Record<string, unknown> = {}) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    const ok = (body: unknown) =>
      Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as unknown as Response);

    if (url.endsWith("/api/knowledge/languages")) {
      return ok(["english", "simple", "spanish"]);
    }
    if (url.endsWith("/api/knowledge") && method === "GET") {
      return ok(overrides.collections ?? COLLECTIONS);
    }
    if (url.endsWith("/api/knowledge/search") && method === "POST") {
      return ok(overrides.hits ?? []);
    }
    if (url.includes("/reindex")) {
      return ok({ jobId: "44444444-4444-4444-4444-444444444444", files: 3 });
    }
    return ok(null);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderKnowledge() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <KnowledgeAdmin />
    </QueryClientProvider>,
  );
}

describe("KnowledgeAdmin", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  it("lists collections with their size, language, and filter keys", async () => {
    stubApi();
    renderKnowledge();

    expect((await screen.findAllByText("ES employment law")).length).toBeGreaterThan(0);
    expect(screen.getByText("spanish")).toBeTruthy();
    expect(screen.getByText(/3 documents/)).toBeTruthy();
    expect(screen.getByText(/42 passages/)).toBeTruthy();
    // Discovered filter keys are shown, because they are what an agent can filter on.
    expect(screen.getByText("jurisdiction")).toBeTruthy();
  });

  it("marks a module-owned collection as such and offers no Delete for it", async () => {
    stubApi();
    renderKnowledge();

    await screen.findAllByText("matter: Acme diligence");
    // The owning module + resource type is surfaced so the read-only state is explained.
    expect(screen.getByText("legal · matter")).toBeTruthy();
    // Exactly one Delete button — the curated collection's. The matter's lifecycle is its module's.
    expect(screen.getAllByRole("button", { name: "Delete" })).toHaveLength(1);
  });

  it("re-indexes a collection and reports the queued job", async () => {
    const fetchMock = stubApi();
    renderKnowledge();

    await screen.findAllByText("ES employment law");
    fireEvent.click(screen.getAllByRole("button", { name: "Re-index" })[0]);

    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some(
          ([url, init]) =>
            String(url).includes("/api/knowledge/11111111-1111-1111-1111-111111111111/reindex") &&
            (init as RequestInit | undefined)?.method === "POST",
        ),
      ).toBe(true);
    });

    expect(await screen.findByText(/Re-indexing 3 document\(s\)/)).toBeTruthy();
  });

  it("sends parsed facet filters with a retrieval preview", async () => {
    const fetchMock = stubApi();
    renderKnowledge();

    await screen.findAllByText("ES employment law");
    fireEvent.change(screen.getByPlaceholderText("What would a user ask?"), {
      target: { value: "notice period" },
    });
    fireEvent.change(screen.getByPlaceholderText("jurisdiction=ES;year=2024"), {
      target: { value: "jurisdiction=ES; effectiveYear=2026" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([url]) => String(url).endsWith("/api/knowledge/search"));
      expect(call).toBeTruthy();
      const body = JSON.parse(String((call![1] as RequestInit).body));
      expect(body.query).toBe("notice period");
      // "key=value; key=value" becomes a real object — the API filters on containment, not text.
      expect(body.filters).toEqual({ jurisdiction: "ES", effectiveYear: "2026" });
    });
  });

  it("explains an empty preview rather than showing nothing", async () => {
    stubApi({ hits: [] });
    renderKnowledge();

    await screen.findAllByText("ES employment law");
    fireEvent.change(screen.getByPlaceholderText("What would a user ask?"), {
      target: { value: "something absent" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByText(/No passages matched/)).toBeTruthy();
  });

  it("creates a curated collection with its language and facets", async () => {
    const fetchMock = stubApi();
    renderKnowledge();

    await screen.findAllByText("ES employment law");
    fireEvent.change(screen.getByPlaceholderText("e.g. Spanish employment law"), {
      target: { value: "DE employment law" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add facet" }));
    fireEvent.change(screen.getByLabelText("New facet key"), { target: { value: "jurisdiction" } });
    fireEvent.change(screen.getByLabelText("Facet value for jurisdiction"), { target: { value: "DE" } });
    fireEvent.click(screen.getByRole("button", { name: "Create collection" }));

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(
        ([url, init]) =>
          String(url).endsWith("/api/knowledge") && (init as RequestInit | undefined)?.method === "POST",
      );
      expect(call).toBeTruthy();
      const body = JSON.parse(String((call![1] as RequestInit).body));
      expect(body.name).toBe("DE employment law");
      expect(body.metadata).toEqual({ jurisdiction: "DE" });
    });
  });

  it("says so when the deployment has no collections at all", async () => {
    stubApi({ collections: [] });
    renderKnowledge();

    expect(await screen.findByText("No collections yet.")).toBeTruthy();
  });
});
