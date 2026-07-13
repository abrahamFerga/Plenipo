// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { AccessDenied } from "./AccessDenied";

describe("AccessDenied", () => {
  afterEach(cleanup);

  it("frames a 401 as not-signed-in", () => {
    render(<AccessDenied status={401} onRetry={() => {}} />);
    expect(screen.getByText("You're not signed in")).toBeTruthy();
  });

  it("frames a 403 as missing permission", () => {
    render(<AccessDenied status={403} onRetry={() => {}} />);
    expect(screen.getByText("You don't have access")).toBeTruthy();
  });

  it("announces itself to assistive tech (role=alert)", () => {
    render(<AccessDenied status={403} onRetry={() => {}} />);
    expect(screen.getByRole("alert")).toBeTruthy();
  });

  it("invokes onRetry when the button is clicked", () => {
    const onRetry = vi.fn();
    render(<AccessDenied status={403} onRetry={onRetry} />);
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(onRetry).toHaveBeenCalledOnce();
  });
});
