import { QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react-native";
import type { ModuleTab } from "@plenipo/client";
import { GenericTab } from "./GenericTab";
import { fakeApi, testQueryClient } from "../test-support";

/**
 * The contract these tests pin: a `TabDescriptor` written in C# produces the same affordances on
 * a phone as it does in a browser.
 *
 * They drive the real renderer against a faked API, so what's under test is the mapping from
 * manifest to UI — not the network, and not the backend. Where a case has a twin in
 * @plenipo/ui's GenericTab tests, it is deliberately asserting the same behaviour.
 */

const api = fakeApi();

beforeEach(() => api.install());
afterEach(() => api.reset());

function renderTab(tab: ModuleTab) {
  return render(
    <QueryClientProvider client={testQueryClient()}>
      <GenericTab tab={tab} />
    </QueryClientProvider>,
  );
}

const listTab = (over: Partial<ModuleTab> = {}): ModuleTab => ({
  id: "matters",
  label: "Matters",
  route: "/legal/matters",
  dataEndpoint: "/api/legal/matters",
  columns: [
    { field: "title", header: "Matter" },
    { field: "client", header: "Client" },
    { field: "status", header: "Status" },
  ],
  ...over,
});

describe("a list tab", () => {
  it("renders each row from the declared columns", async () => {
    api.on("/api/legal/matters", () => [
      { id: "1", title: "Ramirez v. Ortega", client: "Ramirez", status: "Open" },
      { id: "2", title: "Estate of Nkemelu", client: "Nkemelu", status: "Closed" },
    ]);

    renderTab(listTab());

    expect(await screen.findByText("Ramirez v. Ortega")).toBeOnTheScreen();
    expect(screen.getByText("Estate of Nkemelu")).toBeOnTheScreen();
    // The first column is the card title; the next two are labelled pairs.
    expect(screen.getAllByText("Client").length).toBe(2);
  });

  it("shows the manifest's own empty-state wording rather than a generic one", async () => {
    api.on("/api/legal/matters", () => []);

    renderTab(listTab({ placeholder: "No matters yet — open one from chat." }));

    expect(await screen.findByText("No matters yet — open one from chat.")).toBeOnTheScreen();
  });

  it("falls back to the row's own fields when the tab declares no columns", async () => {
    api.on("/api/legal/matters", () => [{ id: "1", reference: "M-100", court: "Superior" }]);

    renderTab(listTab({ columns: [] }));

    // `id` is how the endpoints address the record, not something the reader asked to see.
    expect(await screen.findByText("M-100")).toBeOnTheScreen();
    expect(screen.getByText("court")).toBeOnTheScreen();
    expect(screen.queryByText("id")).toBeNull();
  });

  it("surfaces a failed load as an error, never as an empty tab", async () => {
    // No route registered → 404. An empty state here would claim there are no matters.
    renderTab(listTab());

    expect(await screen.findByRole("alert")).toBeOnTheScreen();
    expect(screen.queryByText("No data yet.")).toBeNull();
  });
});

describe("masked columns", () => {
  it("hides the value until the reader asks for it", async () => {
    api.on("/api/finance/accounts", () => [{ id: "1", name: "Checking", number: "4111111111111234" }]);

    renderTab({
      id: "accounts",
      label: "Accounts",
      route: "/finance/accounts",
      dataEndpoint: "/api/finance/accounts",
      columns: [
        { field: "name", header: "Account" },
        { field: "number", header: "Number", masked: true },
      ],
    });

    expect(await screen.findByText("••••1234")).toBeOnTheScreen();
    expect(screen.queryByText("4111111111111234")).toBeNull();

    fireEvent.press(screen.getByLabelText("Reveal Number"));

    expect(screen.getByText("4111111111111234")).toBeOnTheScreen();
  });
});

describe("row actions", () => {
  const withRowAction = listTab({
    rowActions: [{ id: "close", label: "Close", endpointTemplate: "/api/legal/matters/{id}/close" }],
  });

  it("posts to the row-resolved URL and shows what the endpoint said", async () => {
    api.on("/api/legal/matters", () => [{ id: "42", title: "Ramirez v. Ortega" }]);
    api.on("POST /api/legal/matters/42/close", () => ({ message: "Matter closed." }));

    renderTab(withRowAction);
    fireEvent.press(await screen.findByText("Close"));

    // The {id} placeholder must resolve from the row, not be sent literally.
    await waitFor(() => expect(screen.getByText("Matter closed.")).toBeOnTheScreen());
    expect(api.calls.some((c) => c.url === "/api/legal/matters/42/close" && c.method === "POST")).toBe(true);
  });

  it("asks first when the action declares a confirmation", async () => {
    api.on("/api/legal/matters", () => [{ id: "42", title: "Ramirez v. Ortega" }]);
    api.on("POST /api/legal/matters/42/close", () => ({ message: "Matter closed." }));

    renderTab(
      listTab({
        rowActions: [
          {
            id: "close",
            label: "Close",
            endpointTemplate: "/api/legal/matters/{id}/close",
            confirm: "This closes the matter for everyone.",
          },
        ],
      }),
    );
    fireEvent.press(await screen.findByText("Close"));

    expect(screen.getByText("This closes the matter for everyone.")).toBeOnTheScreen();
    // Nothing has been sent yet — the dialog is a gate, not a notice.
    expect(api.calls.some((c) => c.method === "POST")).toBe(false);
  });
});

describe("the editor", () => {
  const editable = listTab({
    editor: {
      upsertEndpoint: "/api/legal/matters",
      keyField: "title",
      fields: [
        { field: "title", label: "Matter" },
        { field: "hours", label: "Hours", numeric: true, required: false },
      ],
    },
  });

  it("offers Add only when the server sent an editor", async () => {
    api.on("/api/legal/matters", () => []);

    renderTab(listTab());
    await screen.findByText("No data yet.");

    // No editor in the payload means the caller lacks the permission — so there is no button.
    expect(screen.queryByText("Add")).toBeNull();
  });

  it("posts numeric fields as JSON numbers and omits the empty ones", async () => {
    api.on("/api/legal/matters", () => []);
    api.on("POST /api/legal/matters", () => ({}));

    renderTab(editable);
    fireEvent.press(await screen.findByText("Add"));
    fireEvent.changeText(screen.getByLabelText("Matter"), "Nkemelu estate");
    fireEvent.changeText(screen.getByLabelText("Hours"), "3.5");
    fireEvent.press(screen.getByText("Save"));

    await waitFor(() => {
      const post = api.calls.find((c) => c.method === "POST");
      expect(post?.body).toEqual({ title: "Nkemelu estate", hours: 3.5 });
    });
  });

  it("omits an empty optional field rather than sending an empty string", async () => {
    api.on("/api/legal/matters", () => []);
    api.on("POST /api/legal/matters", () => ({}));

    renderTab(editable);
    fireEvent.press(await screen.findByText("Add"));
    fireEvent.changeText(screen.getByLabelText("Matter"), "Nkemelu estate");
    fireEvent.press(screen.getByText("Save"));

    await waitFor(() => {
      const post = api.calls.find((c) => c.method === "POST");
      // "" would bind as 0 for a numeric field and be rejected by a nullable value type.
      expect(post?.body).toEqual({ title: "Nkemelu estate" });
    });
  });

  it("locks the key field while editing — it is the record's identity", async () => {
    api.on("/api/legal/matters", () => [{ id: "1", title: "Ramirez v. Ortega", hours: 2 }]);

    renderTab(editable);
    fireEvent.press(await screen.findByText("Edit"));

    expect(screen.getByLabelText("Matter").props.editable).toBe(false);
    expect(screen.getByLabelText("Hours").props.editable).not.toBe(false);
  });
});

describe("a singleton tab", () => {
  it("renders one config object as a labelled form, not a list", async () => {
    api.on("/api/legal/settings", () => [{ firmName: "Ortega & Co", timeZoneId: "America/Mexico_City" }]);

    renderTab({
      id: "settings",
      label: "Settings",
      route: "/legal/settings",
      dataEndpoint: "/api/legal/settings",
      singleton: true,
      editor: {
        upsertEndpoint: "/api/legal/settings",
        fields: [{ field: "firmName", label: "Firm name" }],
      },
    });

    await waitFor(() => expect(screen.getByLabelText("Firm name").props.value).toBe("Ortega & Co"));
    // An Add button never made sense for a single row.
    expect(screen.queryByText("Add")).toBeNull();
    expect(screen.getByText("Save changes")).toBeOnTheScreen();
  });

  it("shows the values read-only when the caller may not manage them", async () => {
    api.on("/api/legal/settings", () => [{ firmName: "Ortega & Co" }]);

    renderTab({
      id: "settings",
      label: "Settings",
      route: "/legal/settings",
      dataEndpoint: "/api/legal/settings",
      singleton: true,
      columns: [{ field: "firmName", header: "Firm name" }],
    });

    expect(await screen.findByText("Ortega & Co")).toBeOnTheScreen();
    expect(screen.queryByText("Save changes")).toBeNull();
  });
});

describe("the drill-down", () => {
  it("opens the detail document a row points at and can come back", async () => {
    api.on("/api/legal/matters", () => [{ id: "42", title: "Ramirez v. Ortega" }]);
    api.on("/api/legal/matters/42/detail", () => ({
      title: "Ramirez v. Ortega",
      subtitle: "Superior Court",
      sections: [
        { heading: "Summary", text: "Motion to dismiss pending." },
        {
          heading: "Parties",
          columns: [{ field: "name", header: "Name" }],
          rows: [{ name: "Ana Ramirez" }],
        },
      ],
    }));

    renderTab(listTab({ detailEndpoint: "/api/legal/matters/{id}/detail" }));
    fireEvent.press(await screen.findByText("View"));

    expect(await screen.findByText("Motion to dismiss pending.")).toBeOnTheScreen();
    expect(screen.getByText("Ana Ramirez")).toBeOnTheScreen();

    fireEvent.press(screen.getByText("← Back"));
    await waitFor(() => expect(screen.queryByText("Motion to dismiss pending.")).toBeNull());
  });
});

describe("tab actions", () => {
  it("posts and reports the outcome", async () => {
    api.on("/api/legal/matters", () => []);
    api.on("POST /api/legal/imports/run", () => ({ message: "Imported 3 matters." }));

    renderTab(
      listTab({ actions: [{ id: "import", label: "Run import", endpoint: "/api/legal/imports/run" }] }),
    );
    fireEvent.press(await screen.findByText("Run import"));

    await waitFor(() => expect(screen.getByText("Imported 3 matters.")).toBeOnTheScreen());
  });
});

describe("a tab with nothing behind it", () => {
  it("shows the placeholder the manifest wrote", () => {
    renderTab({
      id: "diary",
      label: "Diary",
      route: "/legal/diary",
      placeholder: "Your entries will appear here.",
    });

    expect(screen.getByText("Your entries will appear here.")).toBeOnTheScreen();
  });
});
