import { describe, expect, it } from "vitest";
import { hasPermission } from "./permissions";

describe("hasPermission", () => {
  it("grants on an exact match and denies when absent", () => {
    expect(hasPermission(["chat.use"], "chat.use")).toBe(true);
    expect(hasPermission(["chat.use"], "platform.users.manage")).toBe(false);
    expect(hasPermission([], "chat.use")).toBe(false);
  });

  it("honours the global wildcard (system_admin)", () => {
    expect(hasPermission(["*"], "anything.at.all")).toBe(true);
  });

  it("honours a dotted-prefix wildcard one level up", () => {
    expect(hasPermission(["chat.*"], "chat.approvals.manage")).toBe(true);
    expect(hasPermission(["tools.finance.*"], "tools.finance.categorize")).toBe(true);
  });

  it("walks every level of the hierarchy", () => {
    // tools.* covers tools.finance.categorize even though a level sits between them.
    expect(hasPermission(["tools.*"], "tools.finance.categorize")).toBe(true);
  });

  it("does not match across a different branch", () => {
    expect(hasPermission(["chat.*"], "tools.finance.categorize")).toBe(false);
    expect(hasPermission(["tools.finance.*"], "tools.legal.draft")).toBe(false);
  });

  it("treats only segment-boundary wildcards as grants", () => {
    // Holding the prefix string itself is not a wildcard...
    expect(hasPermission(["tools.finance"], "tools.finance.categorize")).toBe(false);
    // ...and the star must follow a dot, not a partial token.
    expect(hasPermission(["tools.finance*"], "tools.finance.categorize")).toBe(false);
  });
});
