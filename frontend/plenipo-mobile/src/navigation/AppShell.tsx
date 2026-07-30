import { useCallback, useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { BottomTabBar, createBottomTabNavigator } from "@react-navigation/bottom-tabs";
import { createNativeStackNavigator } from "@react-navigation/native-stack";
import { useNavigation, type NavigationProp } from "@react-navigation/native";
import { useQuery } from "@tanstack/react-query";
import { api, type Module, type ModuleTab } from "@plenipo/client";
import { useModules } from "../hooks";
import { useBranding } from "../lib/branding";
import { resolveTabComponent, type ModuleUiRegistry } from "../lib/moduleUi";
import { radius, space, type, useResolvedTheme } from "../theme";
import { ApprovalsScreen } from "../components/ApprovalsScreen";
import { ChatScreen } from "../components/ChatScreen";
import { GenericTab } from "../components/GenericTab";
import { EmptyState, ErrorNote, Loading, Sheet, SheetOption } from "../components/ui";

/**
 * Navigation, built entirely from `GET /api/platform/modules`.
 *
 * There is not one hard-coded route in this file. Install a module in the C# host and its tabs
 * appear in the tab bar; revoke a permission and they disappear — because the manifest arrives
 * already filtered and the navigator is a projection of it. That is the same promise the web
 * shell makes, and it is the whole reason a mobile app for this platform doesn't need to be
 * rewritten per product.
 */

/** Five items is the most that stay tappable on a 320–430pt viewport; the fifth becomes "More". */
const MAX_TAB_ITEMS = 5;

/**
 * The root stack. `Workspace` hosts the tab navigator, so anything that lives beside the tabs
 * rather than inside them — the overflow sheet, the deep-link handler — reaches a tab through
 * nested navigation (`navigate("Workspace", { screen: tabId })`) rather than by name. Navigating
 * by bare name from here would search the STACK, find nothing, and silently do nothing.
 */
type RootParamList = {
  Workspace: { screen?: string } | undefined;
  Approvals: undefined;
};

type RootNavigation = NavigationProp<RootParamList>;

/**
 * A handle on the TAB navigator, for the two things that live beside the tabs rather than inside
 * them: the overflow sheet and the deep-link handler.
 *
 * They can't just call `useNavigation()` — outside `Tab.Navigator` that returns the STACK's
 * navigation, whose `navigate("matters")` searches the stack, finds nothing, and silently does
 * nothing. The tab bar, though, is always rendered *inside* the navigator and is handed its
 * navigation object, so capturing it there is both reliable and cheap.
 *
 * Held in state rather than a ref on purpose: the navigator sets up its own state in an effect, so
 * on the first commit there is no tab navigation yet. A ref would leave a deep link arriving at
 * startup — the notification-tap case, which is the whole point — silently dropped. State makes
 * its arrival re-run the effect that was waiting for it.
 */
type TabNavigation = { navigate: (name: string) => void };

/** The default tab bar, plus a report of the navigation object it was handed. */
function CapturingTabBar({
  onNavigation,
  ...props
}: React.ComponentProps<typeof BottomTabBar> & { onNavigation: (nav: TabNavigation) => void }) {
  const navigation = props.navigation;
  useEffect(() => onNavigation(navigation), [navigation, onNavigation]);
  return <BottomTabBar {...props} />;
}


/** The chat tab is synthesized, not declared — every module has an assistant. */
const CHAT_TAB_ID = "__chat";

const Tab = createBottomTabNavigator();
const Stack = createNativeStackNavigator();

export interface AppShellProps {
  moduleUi?: ModuleUiRegistry;
  /** A pending deep link from a tapped notification, as the notification's app-relative `link`. */
  pendingLink?: string | null;
  onLinkHandled?: () => void;
}

export function AppShell({ moduleUi, pendingLink, onLinkHandled }: AppShellProps) {
  const t = useResolvedTheme();
  const { data: modules, isLoading, isError, error } = useModules();
  const [activeModuleId, setActiveModuleId] = useState<string | null>(null);

  const active = useMemo(
    () => modules?.find((m) => m.id === activeModuleId) ?? modules?.[0] ?? null,
    [modules, activeModuleId],
  );

  if (isLoading) return <Loading label="Loading your workspace…" />;
  if (isError) {
    return (
      <View style={styles.centered}>
        <ErrorNote error={error} />
      </View>
    );
  }
  if (active == null) {
    return (
      <View style={styles.centered}>
        <EmptyState text="No modules are enabled for your account yet." />
      </View>
    );
  }

  return (
    <Stack.Navigator
      screenOptions={{
        headerStyle: { backgroundColor: t.surface },
        headerTintColor: t.text,
        contentStyle: { backgroundColor: t.background },
      }}
    >
      <Stack.Screen name="Workspace" options={{ headerShown: false }}>
        {() => (
          <ModuleTabs
            module={active}
            modules={modules ?? []}
            onSwitchModule={setActiveModuleId}
            moduleUi={moduleUi}
            pendingLink={pendingLink}
            onLinkHandled={onLinkHandled}
          />
        )}
      </Stack.Screen>
      <Stack.Screen name="Approvals" component={ApprovalsScreen} options={{ title: "Approvals" }} />
    </Stack.Navigator>
  );
}

function ModuleTabs({
  module,
  modules,
  onSwitchModule,
  moduleUi,
  pendingLink,
  onLinkHandled,
}: {
  module: Module;
  modules: Module[];
  onSwitchModule: (id: string) => void;
  moduleUi?: ModuleUiRegistry;
  pendingLink?: string | null;
  onLinkHandled?: () => void;
}) {
  const t = useResolvedTheme();
  const [overflowOpen, setOverflowOpen] = useState(false);
  const [switcherOpen, setSwitcherOpen] = useState(false);
  const [tabNavigation, setTabNavigation] = useState<TabNavigation | null>(null);

  // Chat first, then the module's tabs in the order the server sent them (TabDtoMapper already
  // sorted by the descriptor's Order) — the same ordering the web sidebar uses, so the two shells
  // agree on what "first" means.
  const tabs: ModuleTab[] = useMemo(
    () => [{ id: CHAT_TAB_ID, label: "Chat", route: `/${module.id}/chat` }, ...module.tabs],
    [module],
  );

  const overflowing = tabs.length > MAX_TAB_ITEMS;
  const visibleCount = overflowing ? MAX_TAB_ITEMS - 1 : tabs.length;

  // A module the manifest marks Home wins the landing screen; otherwise chat, as the shell's
  // default. Same opt-in rule as the web.
  const initialTab = module.tabs.find((tab) => tab.home === true)?.id ?? CHAT_TAB_ID;

  return (
    <>
      <Tab.Navigator
        initialRouteName={initialTab}
        tabBar={(props) => <CapturingTabBar {...props} onNavigation={setTabNavigation} />}
        screenOptions={{
          headerStyle: { backgroundColor: t.surface },
          headerTintColor: t.text,
          tabBarActiveTintColor: t.brandText,
          tabBarInactiveTintColor: t.textMuted,
          tabBarStyle: { backgroundColor: t.surface, borderTopColor: t.border },
          sceneStyle: { backgroundColor: t.background },
          headerLeft: () => <ModuleButton module={module} onPress={() => setSwitcherOpen(true)} />,
          headerRight: () => <ApprovalsButton />,
          headerTitle: "",
        }}
      >
        {tabs.map((tab, index) => (
          <Tab.Screen
            key={tab.id}
            name={tab.id}
            options={{
              title: tab.label,
              tabBarLabel: tab.label,
              // Every tab is registered so "More" can navigate to it; only the first few get a
              // button. Hiding the button is what makes the overflow sheet work at all.
              ...(index >= visibleCount ? { tabBarButton: () => null } : {}),
            }}
          >
            {() =>
              tab.id === CHAT_TAB_ID ? (
                <ChatScreen module={module} />
              ) : (
                <TabScreen module={module} tab={tab} moduleUi={moduleUi} />
              )
            }
          </Tab.Screen>
        ))}

        {overflowing && (
          <Tab.Screen
            name="__more"
            options={{ tabBarLabel: "More", title: "More" }}
            listeners={{
              // A sheet, not a screen: tapping More opens the list in place rather than
              // navigating somewhere the user then has to back out of.
              tabPress: (e) => {
                e.preventDefault();
                setOverflowOpen(true);
              },
            }}
          >
            {() => <View />}
          </Tab.Screen>
        )}
      </Tab.Navigator>

      <OverflowSheet
        open={overflowOpen}
        tabs={tabs.slice(visibleCount)}
        navigation={tabNavigation}
        onClose={() => setOverflowOpen(false)}
      />

      <Sheet open={switcherOpen} title="Switch module" onClose={() => setSwitcherOpen(false)}>
        {modules.map((m) => (
          <SheetOption
            key={m.id}
            label={m.displayName}
            detail={m.description}
            selected={m.id === module.id}
            onPress={() => {
              onSwitchModule(m.id);
              setSwitcherOpen(false);
            }}
          />
        ))}
      </Sheet>

      <DeepLinkHandler
        tabs={tabs}
        link={pendingLink}
        navigation={tabNavigation}
        onHandled={onLinkHandled}
      />
    </>
  );
}

/** The destinations that didn't fit in the tab bar. */
function OverflowSheet({
  open,
  tabs,
  navigation,
  onClose,
}: {
  open: boolean;
  tabs: ModuleTab[];
  navigation: TabNavigation | null;
  onClose: () => void;
}) {
  return (
    <Sheet open={open} title="More" onClose={onClose}>
      {tabs.map((tab) => (
        <SheetOption
          key={tab.id}
          label={tab.label}
          onPress={() => {
            onClose();
            navigation?.navigate(tab.id);
          }}
        />
      ))}
    </Sheet>
  );
}

/**
 * Resolves a notification's app-relative `link` to a tab and navigates there.
 *
 * Matching is longest-prefix against the tabs' declared `route`s, because a link points at a
 * RECORD ("/legal/matters/42") while a route names a LIST ("/legal/matters"). Landing on the list
 * that contains the record is honest and always works; guessing at a detail screen from a URL the
 * manifest never promised would not.
 */
function DeepLinkHandler({
  tabs,
  link,
  navigation,
  onHandled,
}: {
  tabs: ModuleTab[];
  link?: string | null;
  navigation: TabNavigation | null;
  onHandled?: () => void;
}) {
  const resolve = useCallback(
    (target: string): ModuleTab | undefined =>
      [...tabs]
        .filter((tab) => target === tab.route || target.startsWith(`${tab.route}/`))
        .sort((a, b) => b.route.length - a.route.length)[0],
    [tabs],
  );

  useEffect(() => {
    // Wait for the navigator: a link that arrived before it was ready is still pending, not
    // handled. Marking it handled here is how a notification tap at cold start gets lost.
    if (link == null || link === "" || navigation == null) return;
    const tab = resolve(link);
    if (tab != null) navigation?.navigate(tab.id);
    // Handled either way: an unresolvable link must not be retried forever on every render.
    onHandled?.();
  }, [link, navigation, onHandled, resolve]);

  return null;
}

function ModuleButton({ module, onPress }: { module: Module; onPress: () => void }) {
  const t = useResolvedTheme();
  const branding = useBranding();
  const { data: fallback } = useQuery({
    queryKey: ["branding"],
    queryFn: () => api.branding(),
    staleTime: Infinity,
    enabled: branding.name == null && branding.logo == null,
  });

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`Switch module. Current: ${module.displayName}`}
      onPress={onPress}
      style={styles.headerButton}
    >
      {branding.logo ?? (
        <Text style={{ ...type.caption, color: t.textMuted }}>
          {branding.name ?? fallback?.name ?? "Plenipo"}
        </Text>
      )}
      <Text style={{ ...type.heading, color: t.text }}>{module.displayName} ▾</Text>
    </Pressable>
  );
}

/**
 * The approvals affordance, always in the header rather than buried in a tab. A parked write is
 * the one thing in this app that is blocking someone, so its count is never more than a glance
 * away.
 */
function ApprovalsButton() {
  const t = useResolvedTheme();
  const navigation = useNavigation<RootNavigation>();
  const { data } = useQuery({
    queryKey: ["approvals"],
    queryFn: () => api.approvals.list(),
    refetchInterval: 30_000,
  });
  const count = data?.length ?? 0;

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={count === 0 ? "Approvals" : `Approvals, ${count} waiting`}
      onPress={() => navigation.navigate("Approvals")}
      style={styles.headerButton}
      hitSlop={8}
    >
      <View style={styles.badgeRow}>
        <Text style={{ ...type.label, color: t.brandText }}>Approvals</Text>
        {/* The count is never color-only — the number itself carries the signal. */}
        {count > 0 && (
          <View style={[styles.badge, { backgroundColor: t.danger }]}>
            <Text style={{ ...type.caption, color: "#ffffff", fontWeight: "700" }}>{count}</Text>
          </View>
        )}
      </View>
    </Pressable>
  );
}

/** A tab: the product's registered screen when there is one, otherwise the generic renderer. */
function TabScreen({
  module,
  tab,
  moduleUi,
}: {
  module: Module;
  tab: ModuleTab;
  moduleUi?: ModuleUiRegistry;
}) {
  const Custom = resolveTabComponent(moduleUi, module.id, tab.id);
  return Custom != null ? <Custom moduleId={module.id} tab={tab} /> : <GenericTab tab={tab} />;
}

const styles = StyleSheet.create({
  centered: { flex: 1, justifyContent: "center", padding: space.lg },
  headerButton: { paddingHorizontal: space.md, paddingVertical: space.xs, justifyContent: "center" },
  badgeRow: { flexDirection: "row", alignItems: "center", gap: space.xs },
  badge: { minWidth: 20, paddingHorizontal: 5, borderRadius: radius.sm, alignItems: "center" },
});
