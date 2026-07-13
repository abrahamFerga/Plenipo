// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { AppErrorBoundary } from "./AppErrorBoundary";

describe("AppErrorBoundary", () => {
  beforeEach(() => {
    // React logs caught render errors to console.error; silence the expected noise.
    vi.spyOn(console, "error").mockImplementation(() => {});
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it("renders its children when nothing throws", () => {
    render(
      <AppErrorBoundary>
        <div>shell content</div>
      </AppErrorBoundary>,
    );
    expect(screen.getByText("shell content")).toBeTruthy();
  });

  it("shows a full-screen recovery card instead of a blank page when the shell throws", () => {
    const Boom = () => {
      throw new Error("shell exploded");
    };
    render(
      <AppErrorBoundary>
        <Boom />
      </AppErrorBoundary>,
    );

    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.getByText("Something went wrong")).toBeTruthy();
    // The underlying message reaches the user, and the only sensible action for a shell crash is a reload.
    expect(screen.getByText("shell exploded")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reload" })).toBeTruthy();
  });
});
