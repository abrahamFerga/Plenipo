// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { FieldInput } from "./FieldInput";
import type { TabEditorField } from "../lib/api";

function renderField(field: TabEditorField, rows: unknown = []) {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(rows) } as unknown as Response),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onChange = vi.fn();
  render(
    <QueryClientProvider client={client}>
      <FieldInput id="f" field={field} value="" onChange={onChange} />
    </QueryClientProvider>,
  );
  return onChange;
}

describe("FieldInput", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders a select for a fixed vocabulary and never pre-picks", () => {
    const onChange = renderField({ field: "direction", label: "Direction", options: ["expense", "income"] });

    const select = screen.getByRole("combobox") as HTMLSelectElement;
    expect(select.value).toBe(""); // the blank "Choose…" entry
    fireEvent.change(select, { target: { value: "income" } });
    expect(onChange).toHaveBeenCalledWith("income");
  });

  it("draws dynamic options from the endpoint's rows", async () => {
    renderField(
      { field: "accountName", label: "Account", optionsEndpoint: "/api/finance/accounts", optionsField: "name" },
      [{ name: "Citibanamex" }, { name: "Cash" }],
    );

    await waitFor(() => expect(screen.getByRole("option", { name: "Citibanamex" })).toBeTruthy());
    expect(screen.getByRole("option", { name: "Cash" })).toBeTruthy();
  });

  it("says honestly when there is nothing to choose from yet", async () => {
    renderField(
      { field: "accountName", label: "Account", optionsEndpoint: "/api/finance/accounts", optionsField: "name" },
      [],
    );

    await waitFor(() => expect(screen.getByText(/Nothing exists yet to pick from/)).toBeTruthy());
    expect((screen.getByRole("combobox") as HTMLSelectElement).disabled).toBe(true);
  });

  it("stays a plain input when no options are declared", () => {
    renderField({ field: "name", label: "Name" });
    expect(screen.queryByRole("combobox")).toBeNull();
    expect(screen.getByRole("textbox")).toBeTruthy();
  });

  it("masked fields type password-style until explicitly revealed", () => {
    const { container } = renderFieldWithResult({ field: "accountNumber", label: "Account number", masked: true });

    const input = container.querySelector("input")!;
    expect(input.type).toBe("password");

    fireEvent.click(screen.getByRole("button", { name: "Reveal Account number" }));
    expect(input.type).toBe("text");

    fireEvent.click(screen.getByRole("button", { name: "Hide Account number" }));
    expect(input.type).toBe("password");
  });
});

function renderFieldWithResult(field: TabEditorField) {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve([]) } as unknown as Response),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <FieldInput id="f" field={field} value="" onChange={vi.fn()} />
    </QueryClientProvider>,
  );
}
