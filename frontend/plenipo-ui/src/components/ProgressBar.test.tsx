// @vitest-environment jsdom
import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { ProgressBar } from "./ProgressBar";

describe("ProgressBar", () => {
  afterEach(cleanup);

  it("reads healthy under the warn threshold, with the remainder stated as text", () => {
    render(<ProgressBar label="Groceries" value={200} max={400} />);

    expect(screen.getByTestId("progress-status").textContent).toContain("200 left");
    expect(screen.getByTestId("progress-fill").className).toContain("emerald");
    expect(screen.getByTestId("progress-fill").style.width).toBe("50%");
  });

  it("turns warning from warnAt up to the target", () => {
    render(<ProgressBar label="Dining" value={360} max={400} />);

    expect(screen.getByTestId("progress-fill").className).toContain("amber");
    expect(screen.getByTestId("progress-status").textContent).toContain("40 left");
  });

  it("goes critical past the target, capping the track and stating the overage", () => {
    render(<ProgressBar label="Transit" value={440} max={400} />);

    expect(screen.getByTestId("progress-fill").className).toContain("red");
    // The bar never draws past 100% — the overage is text, not overflow.
    expect(screen.getByTestId("progress-fill").style.width).toBe("100%");
    expect(screen.getByTestId("progress-status").textContent).toContain("Over by 40");
  });

  it("never signals by color alone — every band carries an icon and text", () => {
    render(<ProgressBar label="Transit" value={440} max={400} />);

    const status = screen.getByTestId("progress-status");
    expect(status.querySelector("svg")).toBeTruthy();
    expect(status.textContent?.trim()).not.toBe("");
  });

  it("exposes progress semantics for assistive tech", () => {
    render(<ProgressBar label="Groceries" value={200} max={400} />);

    const bar = screen.getByRole("progressbar", { name: "Groceries" });
    expect(bar.getAttribute("aria-valuenow")).toBe("50");
    expect(bar.getAttribute("aria-valuetext")).toContain("200 of 400");
  });

  it("prefers caller-supplied status text over the derived phrasing", () => {
    render(<ProgressBar label="Groceries" value={200} max={400} text="$200.00 left this month" />);

    expect(screen.getByTestId("progress-status").textContent).toContain("$200.00 left this month");
  });
});
