// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { TabErrorBoundary } from "./TabErrorBoundary";

describe("TabErrorBoundary", () => {
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
      <TabErrorBoundary>
        <div>healthy content</div>
      </TabErrorBoundary>,
    );
    expect(screen.getByText("healthy content")).toBeTruthy();
  });

  it("contains a crash to a labelled error card instead of unmounting the tree", () => {
    const Boom = () => {
      throw new Error("kaboom");
    };
    render(
      <TabErrorBoundary label="Transactions">
        <Boom />
      </TabErrorBoundary>,
    );

    expect(screen.getByText("This view failed to load: Transactions")).toBeTruthy();
    expect(screen.getByText("kaboom")).toBeTruthy();
  });

  it("re-attempts the view when 'Try again' is clicked", () => {
    let shouldThrow = true;
    const Flaky = () => {
      if (shouldThrow) throw new Error("transient");
      return <div>recovered</div>;
    };

    render(
      <TabErrorBoundary>
        <Flaky />
      </TabErrorBoundary>,
    );
    expect(screen.getByText("transient")).toBeTruthy();

    // The underlying cause is gone; retrying clears the boundary and re-renders successfully.
    shouldThrow = false;
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(screen.getByText("recovered")).toBeTruthy();
  });
});
