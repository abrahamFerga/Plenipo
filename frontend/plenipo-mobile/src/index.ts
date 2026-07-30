// ─────────────────────────────────────────────────────────────────────────────
// @plenipo/mobile — the server-driven React Native shell
//
// The same idea as @plenipo/ui, rendered natively: this package hardcodes no
// domain routes, no screens, and no vocabulary. It reads GET /api/platform/modules
// — already filtered by the caller's permissions — and builds the tab bar, the
// tables, the forms, the charts and the chat from it.
//
// So a product's mobile app is a config object and a brand. Add a tab to a C#
// ModuleManifest and it appears on every installed phone after a backend deploy;
// a module needing something the generic renderer can't express registers one
// native screen with `defineModule` and keeps the rest.
//
// Nothing here can widen access. The manifest arrives permission-filtered, tools
// are chosen server-side before the model is called, and writes ride the approval
// lane — the client only renders what it was handed.
// ─────────────────────────────────────────────────────────────────────────────

// The root component, and the shell alone for apps that own their navigation container.
export { PlenipoMobileApp } from "./PlenipoMobileApp";
export type { PlenipoMobileAppProps } from "./PlenipoMobileApp";
export { AppShell } from "./navigation/AppShell";
export type { AppShellProps } from "./navigation/AppShell";

// Configuration and the platform seams the shell needs but never imports.
export { configurePlenipoMobile, intlLocale, isConfigured, mobileConfig, resetMobileConfig } from "./config";
export type { PlenipoMobileConfig } from "./config";
export { devAuthOnly, memoryStorage } from "./adapters";
export type {
  AuthAdapter,
  DeviceAdapter,
  LocaleAdapter,
  MobilePlatform,
  PlenipoAdapters,
  PushAdapter,
  SecureStorageAdapter,
} from "./adapters";

// The product-extension registry — identical in shape to the web shell's.
export { createModuleUiRegistry, defineModule, resolveTabComponent } from "./lib/moduleUi";
export type { ModuleTabProps, ModuleUiRegistry, PlenipoModuleUi } from "./lib/moduleUi";

// Branding (content) and theme (tokens) — the two halves of making the shell yours.
export { BrandingContext, useBranding } from "./lib/branding";
export type { PlenipoBranding } from "./lib/branding";
export {
  darkTheme,
  HIT_SIZE,
  lightTheme,
  radius,
  resolveTheme,
  space,
  type,
  useResolvedTheme,
} from "./theme";
export type { PlenipoTheme, PlenipoThemeOverride } from "./theme";

// Screens and renderers, for products composing their own navigation.
export { GenericTab } from "./components/GenericTab";
export { TabChartView } from "./components/TabChart";
export { ChatScreen } from "./components/ChatScreen";
export { ApprovalsScreen } from "./components/ApprovalsScreen";
export { FieldInput } from "./components/FieldInput";

// Primitives, so a registered screen looks like the rest of the shell instead of near it.
export { Button, Card, ConfirmDialog, EmptyState, ErrorNote, Loading, OutcomeNote, Sheet, SheetOption } from "./components/ui";

// Data hooks, mirroring @plenipo/ui's.
export { useInfo, useMe, useModules, usePermission } from "./hooks";

// Push device registration.
export { forgetThisDevice, installationId, useDeviceRegistration } from "./push/useDeviceRegistration";

// The manifest contract itself, re-exported so a product imports types from one place.
export * from "@plenipo/client";
