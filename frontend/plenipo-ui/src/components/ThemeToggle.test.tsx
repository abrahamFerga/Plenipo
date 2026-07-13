// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { ThemeToggle } from "./ThemeToggle";
import { initTheme, resolveTheme, setThemePreference } from "../lib/theme";

/** jsdom has no matchMedia; emulate an OS whose scheme we control. */
function stubMatchMedia(prefersDark: boolean) {
  vi.stubGlobal(
    "matchMedia",
    vi.fn().mockReturnValue({
      matches: prefersDark,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    }),
  );
}

describe("theme", () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.classList.remove("dark");
    stubMatchMedia(false);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("defaults to the system preference", () => {
    stubMatchMedia(true);
    initTheme();
    expect(document.documentElement.classList.contains("dark")).toBe(true);
    expect(resolveTheme("system")).toBe("dark");
  });

  it("an explicit preference wins over the OS and persists", () => {
    stubMatchMedia(true);
    initTheme();
    setThemePreference("light");

    expect(document.documentElement.classList.contains("dark")).toBe(false);
    expect(localStorage.getItem("plenipo.theme")).toBe("light");
  });

  it("restores the stored preference on startup", () => {
    localStorage.setItem("plenipo.theme", "dark");
    initTheme();
    expect(document.documentElement.classList.contains("dark")).toBe(true);
  });

  it("the toggle cycles light → dark → system and applies each", () => {
    initTheme();
    setThemePreference("light");
    render(<ThemeToggle />);

    const button = screen.getByRole("button", { name: /light theme/i });
    fireEvent.click(button); // light → dark
    expect(document.documentElement.classList.contains("dark")).toBe(true);
    expect(localStorage.getItem("plenipo.theme")).toBe("dark");

    fireEvent.click(screen.getByRole("button", { name: /dark theme/i })); // dark → system
    expect(localStorage.getItem("plenipo.theme")).toBe("system");
    // stubbed OS prefers light, so "system" lands on light
    expect(document.documentElement.classList.contains("dark")).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: /system theme/i })); // system → light
    expect(localStorage.getItem("plenipo.theme")).toBe("light");
  });
});
