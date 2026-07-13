// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { Markdown } from "./Markdown";

describe("Markdown (assistant response rendering)", () => {
  afterEach(cleanup);

  it("renders bold, inline code, and fenced code blocks", () => {
    render(<Markdown>{"**bold** text with `inline` and:\n\n```\nconst x = 1;\n```"}</Markdown>);

    expect(screen.getByText("bold").tagName).toBe("STRONG");
    expect(screen.getByText("inline").tagName).toBe("CODE");
    // A fenced block renders inside a <pre>.
    expect(screen.getByText(/const x = 1/).closest("pre")).not.toBeNull();
  });

  it("renders a bulleted list as list items", () => {
    render(<Markdown>{"- one\n- two"}</Markdown>);
    expect(screen.getByText("one").closest("li")).not.toBeNull();
    expect(screen.getByText("two").closest("li")).not.toBeNull();
  });

  it("copies a code block's contents when its copy button is clicked", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });

    render(<Markdown>{"```\nconst x = 1;\n```"}</Markdown>);
    fireEvent.click(screen.getByRole("button", { name: "Copy code" }));

    await waitFor(() => expect(writeText).toHaveBeenCalled());
    expect(writeText.mock.calls[0][0]).toContain("const x = 1;");
    // The button acknowledges the copy.
    expect(await screen.findByText("Copied!")).toBeTruthy();
  });

  it("does not inject raw HTML from model output (XSS-safe)", () => {
    render(<Markdown>{'<img src=x onerror="alert(1)"> hello there'}</Markdown>);

    // The raw HTML is not rendered as a real element; only the text survives.
    expect(document.querySelector("img")).toBeNull();
    expect(screen.getByText(/hello there/)).toBeTruthy();
  });

  it("sanitizes dangerous link protocols (javascript:) from model output", () => {
    render(<Markdown>{"[click me](javascript:alert(1))"}</Markdown>);

    // react-markdown's default urlTransform strips the javascript: URL, so the link is never executable.
    const link = screen.getByText("click me").closest("a");
    expect(link).not.toBeNull();
    expect(link?.getAttribute("href") ?? "").not.toContain("javascript:");
  });
});
