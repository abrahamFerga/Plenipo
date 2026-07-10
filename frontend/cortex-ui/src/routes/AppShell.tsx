import { useEffect, useMemo, useRef, useState } from "react";
import { matchPath, Navigate, Route, Routes, useLocation, useNavigate } from "react-router-dom";
import { TopBar } from "../components/TopBar";
import { Sidebar } from "../components/Sidebar";
import { ChatView } from "../components/ChatView";
import { ConnectedAccounts } from "../components/ConnectedAccounts";
import { ModuleTabView } from "../components/ModuleTabView";
import { DemoModeBanner } from "../components/DemoModeBanner";
import { OnboardingOffer } from "../components/OnboardingOffer";
import { OnboardingWizard } from "../components/OnboardingWizard";
import { ApiUnreachable } from "../components/ApiUnreachable";
import { AccessDenied } from "../components/AccessDenied";
import { useModules } from "../hooks/useModules";
import { useInfo } from "../hooks/useInfo";
import { ApiError, type Module, type ModuleTab } from "../lib/api";
import {
  createModuleUiRegistry,
  resolveTabComponent,
  type CortexModuleUi,
} from "../lib/moduleUi";
import { ActiveModuleContext, resolveActiveModuleId } from "../lib/activeModule";
import { BrandingContext, type CortexBranding } from "../lib/branding";

/** The Chat tab, injected first when chat is enabled (the shell is chat-first). */
const CHAT_TAB: ModuleTab = {
  id: "chat",
  label: "Chat",
  route: "/chat",
};

/**
 * The active module's tabs for the sidebar/routes: chat first (when enabled), then the module's own
 * tabs. A module's own "chat" tab is dropped — the shell owns the chat surface.
 */
function tabsFor(module: Module | undefined, chatEnabled: boolean): ModuleTab[] {
  const rest = module ? module.tabs.filter((t) => t.id !== "chat") : [];
  return chatEnabled ? [CHAT_TAB, ...rest] : rest;
}

interface AppShellProps {
  /**
   * Host-registered UI for modules — custom React components per tab. Any tab without a
   * registered component falls back to the built-in server-driven `GenericTab`. The server
   * manifest remains the source of truth for which tabs exist and who can see them.
   */
  moduleUi?: readonly CortexModuleUi[];
  /** Product name + logo shown in the top bar. Lets a host present its own identity, not "Cortex". */
  branding?: CortexBranding;
}

export function AppShell({ moduleUi, branding }: AppShellProps = {}) {
  const { data: modules, isLoading, isError, error, refetch } = useModules();
  const { data: info, isLoading: infoLoading } = useInfo();
  // The module the user explicitly picked in the switcher — remembered across module-agnostic
  // routes like /chat. Everything else about the active module is derived from the URL below.
  const [selectedModuleId, setSelectedModuleId] = useState<string | undefined>(
    undefined,
  );
  // Mobile-only navigation drawer (the sidebar is always visible at md+).
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const navigate = useNavigate();
  const { pathname } = useLocation();

  const uiRegistry = useMemo(() => createModuleUiRegistry(moduleUi), [moduleUi]);

  // Chat is available unless the deployment configured no AI provider (Provider=None → chatEnabled
  // false). Default to enabled when /info is unavailable — the shell is chat-first.
  const chatEnabled = info?.chatEnabled ?? true;

  // Derive the active module every render (not via an effect) so a deep-linked or refreshed tab
  // route resolves to its module immediately — before the catch-all "*" redirect can fire.
  const activeModuleId = resolveActiveModuleId(modules, selectedModuleId, pathname);

  const activeModule = useMemo(
    () => modules?.find((m) => m.id === activeModuleId),
    [modules, activeModuleId],
  );

  const tabs = useMemo(() => tabsFor(activeModule, chatEnabled), [activeModule, chatEnabled]);

  // Reflect the current tab in the document title so history, bookmarks, and screen readers name
  // the page. `matchPath` handles parameterized tab routes (e.g. `/finance/accounts/:id`).
  useEffect(() => {
    const productName = branding?.name ?? "Cortex";
    const tab = tabs.find((t) => matchPath(t.route, pathname));
    document.title = tab ? `${tab.label} · ${productName}` : productName;
  }, [pathname, tabs, branding]);

  // Move focus to the main landmark after in-app navigation (not the initial render) so screen
  // readers announce the new page. The skip link still targets the same landmark.
  const mainRef = useRef<HTMLElement>(null);
  const firstRenderRef = useRef(true);
  useEffect(() => {
    if (firstRenderRef.current) {
      firstRenderRef.current = false;
      return;
    }
    mainRef.current?.focus({ preventScroll: true });
  }, [pathname]);

  // Switching modules from the top-bar switcher: remember the choice and jump to its first visible tab.
  function changeModule(id: string) {
    setSelectedModuleId(id);
    const next = modules?.find((m) => m.id === id);
    const first = tabsFor(next, chatEnabled)[0];
    if (first) {
      navigate(first.route);
    }
  }

  // Wait for both the manifest and deployment info so the first routed render already knows whether
  // chat exists — otherwise a no-AI deployment would briefly land on /chat before hiding it.
  // (NoModulesNotice below explains the OTHER blank-chat cause: a host with no domain modules.)
  if (isLoading || infoLoading) {
    return (
      <div role="status" className="grid h-full place-items-center text-sm text-slate-500">
        Loading…
      </div>
    );
  }

  if (isError) {
    const retry = () => void refetch();
    // A 401/403 means the API is reachable but rejected us — an auth problem, not connectivity.
    if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
      return <AccessDenied status={error.status} message={error.message} onRetry={retry} />;
    }
    return <ApiUnreachable message={(error as Error).message} onRetry={retry} />;
  }

  return (
    <BrandingContext.Provider value={branding ?? {}}>
      <ActiveModuleContext.Provider
        value={{
          modules: modules ?? [],
          activeModule,
          activeModuleId,
          setActiveModuleId: changeModule,
        }}
      >
      <div className="flex h-full flex-col bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
        {/* Keyboard/screen-reader users bypass the nav and jump straight to the tab content. */}
        <a
          href="#main-content"
          className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-brand-600 focus:px-3 focus:py-2 focus:text-sm focus:font-medium focus:text-white"
        >
          Skip to content
        </a>
        <TopBar
          sidebarOpen={sidebarOpen}
          onToggleSidebar={() => setSidebarOpen((open) => !open)}
        />
        <DemoModeBanner />
        <OnboardingOffer module={activeModule} />
        <div className="flex min-h-0 flex-1">
          <Sidebar
            moduleId={activeModuleId}
            tabs={tabs}
            open={sidebarOpen}
            onClose={() => setSidebarOpen(false)}
            onNavigate={() => setSidebarOpen(false)}
          />
          <main
            id="main-content"
            ref={mainRef}
            tabIndex={-1}
            aria-label="Workspace"
            className="min-h-0 flex-1 overflow-y-auto p-6 focus:outline-none"
          >
            <Routes>
              {chatEnabled && (
                <Route
                  path="/chat"
                  element={
                    activeModuleId ? (
                      <ChatView
                        key={activeModuleId}
                        moduleId={activeModuleId}
                        suggestedPrompts={activeModule?.suggestedPrompts}
                        agents={activeModule?.agents}
                        skills={activeModule?.skills}
                      />
                    ) : (
                      <NoModulesNotice anyInstalled={(modules?.length ?? 0) > 0} />
                    )
                  }
                />
              )}
              {/* Module-agnostic, like /chat: the caller's own delegated-connector logins. */}
              <Route path="/account/connections" element={<ConnectedAccounts />} />
              {/* The active module's first-run setup wizard, when it declares one. */}
              {activeModule?.onboarding && (
                <Route
                  path="/setup"
                  element={
                    <OnboardingWizard
                      module={activeModule}
                      onDone={() => {
                        const first = tabs.find((t) => t.id !== "chat") ?? tabs[0];
                        navigate(first?.route ?? "/chat");
                      }}
                    />
                  }
                />
              )}
              {/* Dynamic routes generated from the active module's tabs. Each renders a
                  host-registered component when one exists, else the generic server-driven view. */}
              {tabs
                .filter((t) => t.id !== "chat")
                .map((tab) => (
                  <Route
                    key={tab.id}
                    path={tab.route}
                    element={
                      <ModuleTabView
                        moduleId={activeModuleId ?? ""}
                        tab={tab}
                        component={resolveTabComponent(uiRegistry, activeModuleId, tab.id)}
                      />
                    }
                  />
                ))}
              {/* Land on the first available tab — chat when enabled, else the first module tab; a
                  module with no visible tabs shows an empty state rather than looping the redirect. */}
              <Route
                path="*"
                element={
                  tabs.length > 0 ? (
                    <Navigate to={tabs[0].route} replace />
                  ) : (
                    <div className="grid h-full place-items-center text-sm text-slate-500">
                      Nothing to show yet.
                    </div>
                  )
                }
              />
            </Routes>
          </main>
        </div>
      </div>
      </ActiveModuleContext.Provider>
    </BrandingContext.Provider>
  );
}

/**
 * Why the chat pane would otherwise be blank: the assistant is always a MODULE's assistant, so a
 * host with no domain modules (the bare platform host) — or with every module disabled for this
 * tenant — has nothing to chat with. Say so, and say what to do about it.
 */
function NoModulesNotice({ anyInstalled }: { anyInstalled: boolean }) {
  return (
    <div className="grid h-full place-items-center">
      <div className="max-w-md rounded-lg border border-dashed border-slate-300 p-8 text-center dark:border-slate-700">
        <p className="text-base font-semibold text-slate-900 dark:text-slate-100">
          No modules to chat with
        </p>
        <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
          {anyInstalled
            ? "Modules are installed but none is enabled for this workspace. An administrator can enable one under Admin → Modules."
            : "This deployment has no domain modules installed — the assistant is always a module's assistant. " +
              "A product host installs its modules with AddCortexModule<T>(); if you operate this deployment, " +
              "check the host's Program.cs."}
        </p>
      </div>
    </div>
  );
}
