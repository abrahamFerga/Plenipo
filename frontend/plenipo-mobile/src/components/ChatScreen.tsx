import { useCallback, useEffect, useRef, useState } from "react";
import {
  ActivityIndicator,
  FlatList,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
} from "react-native";
import { useQueryClient } from "@tanstack/react-query";
import { messageId, runAgui, type Module, type ModuleAgent } from "@plenipo/client";
import { HIT_SIZE, radius, space, type, useResolvedTheme } from "../theme";
import { Button, Sheet, SheetOption } from "./ui";

/**
 * The chat, over AG-UI.
 *
 * AG-UI is plain HTTP POST + SSE, which is the whole reason it's the mobile transport: a
 * WebSocket on a phone dies on every backgrounding, network hand-off, and lock-screen, and the
 * SignalR hub the web shell uses would spend its life reconnecting. Both transports drive the
 * same `AuthorizedAgentRunner` server-side, so RBAC tool-filtering, approval gating, auditing and
 * token accounting are identical either way — the choice here is purely about the radio.
 *
 * What the user sees of the security spine: tools that ran appear as chips, and a tool that was
 * BLOCKED pending approval refreshes the approvals list instead of silently doing nothing.
 */

interface ChatMessage {
  id: string;
  role: "user" | "assistant";
  text: string;
  tools: string[];
  failed?: boolean;
}

export function ChatScreen({ module }: { module: Module }) {
  const t = useResolvedTheme();
  const qc = useQueryClient();
  const listRef = useRef<FlatList<ChatMessage>>(null);

  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [draft, setDraft] = useState("");
  const [streaming, setStreaming] = useState(false);
  const [agent, setAgent] = useState<string | null>(defaultAgentName(module.agents));
  const [pickingAgent, setPickingAgent] = useState(false);

  // The conversation the server resumes. Before the first turn completes we own a thread id so a
  // fast second message joins the same conversation instead of starting a new one.
  const conversationId = useRef<string | undefined>(undefined);
  const threadId = useRef<string | undefined>(undefined);
  const abort = useRef<AbortController | null>(null);

  // A stream must not outlive the screen: leaving mid-answer aborts the request rather than
  // leaving it writing into an unmounted component.
  useEffect(() => () => abort.current?.abort(), []);

  // Switching modules is switching assistants — the previous transcript belongs to the old one.
  useEffect(() => {
    abort.current?.abort();
    setMessages([]);
    setStreaming(false);
    conversationId.current = undefined;
    threadId.current = undefined;
    setAgent(defaultAgentName(module.agents));
  }, [module.id, module.agents]);

  const patchAssistant = useCallback((id: string, update: (m: ChatMessage) => ChatMessage) => {
    setMessages((prev) => prev.map((m) => (m.id === id ? update(m) : m)));
  }, []);

  const send = useCallback(
    (text: string) => {
      const trimmed = text.trim();
      if (trimmed === "" || streaming) return;

      const assistantId = messageId();
      setMessages((prev) => [
        ...prev,
        { id: messageId(), role: "user", text: trimmed, tools: [] },
        { id: assistantId, role: "assistant", text: "", tools: [] },
      ]);
      setDraft("");
      setStreaming(true);

      const controller = new AbortController();
      abort.current = controller;
      const thread = conversationId.current ?? (threadId.current ??= messageId());

      void (async () => {
        try {
          for await (const evt of runAgui(module.id, trimmed, {
            threadId: thread,
            signal: controller.signal,
            agent: agent ?? undefined,
          })) {
            switch (evt.type) {
              case "TEXT_MESSAGE_CONTENT": {
                const delta = typeof evt.delta === "string" ? evt.delta : "";
                if (delta !== "") patchAssistant(assistantId, (m) => ({ ...m, text: m.text + delta }));
                break;
              }
              case "TOOL_CALL_START": {
                const tool = typeof evt.toolCallName === "string" ? evt.toolCallName : undefined;
                if (tool != null) patchAssistant(assistantId, (m) => ({ ...m, tools: [...m.tools, tool] }));
                break;
              }
              case "CUSTOM": {
                // A side-effecting tool was parked for a human. The approvals list is now stale,
                // and the badge on that tab is how the user finds out.
                if (evt.name === "approval_required") {
                  void qc.invalidateQueries({ queryKey: ["approvals"] });
                }
                break;
              }
              case "RUN_FINISHED": {
                const result = evt.result as { conversationId?: string } | undefined;
                if (result?.conversationId != null) conversationId.current = result.conversationId;
                break;
              }
              case "RUN_ERROR": {
                patchAssistant(assistantId, (m) => ({
                  ...m,
                  failed: true,
                  text: typeof evt.message === "string" ? evt.message : "Unknown stream error",
                }));
                break;
              }
            }
          }
        } catch (e) {
          if (!controller.signal.aborted) {
            patchAssistant(assistantId, (m) => ({
              ...m,
              failed: true,
              text: e instanceof Error ? e.message : String(e),
            }));
          }
        } finally {
          abort.current = null;
          setStreaming(false);
        }
      })();
    },
    [agent, module.id, patchAssistant, qc, streaming],
  );

  const agents = module.agents ?? [];
  const showStarters = messages.length === 0 && (module.suggestedPrompts?.length ?? 0) > 0;

  return (
    <KeyboardAvoidingView
      style={{ flex: 1 }}
      behavior={Platform.OS === "ios" ? "padding" : undefined}
      keyboardVerticalOffset={Platform.OS === "ios" ? 96 : 0}
    >
      <FlatList
        ref={listRef}
        data={messages}
        keyExtractor={(m) => m.id}
        contentContainerStyle={styles.transcript}
        onContentSizeChange={() => listRef.current?.scrollToEnd({ animated: true })}
        renderItem={({ item }) => <Bubble message={item} streaming={streaming} />}
        ListEmptyComponent={
          <View style={styles.intro}>
            <Text style={{ ...type.heading, color: t.text }}>{module.displayName}</Text>
            {module.description != null && (
              <Text style={{ ...type.body, color: t.textMuted, marginTop: space.xs }}>
                {module.description}
              </Text>
            )}
          </View>
        }
      />

      {/* Starters exist so a newcomer can exercise the module's tools without knowing what to
          type — they come from the manifest, so every module gets them for free. */}
      {showStarters && (
        <View style={styles.starters}>
          {module.suggestedPrompts!.map((prompt) => (
            <Pressable
              key={prompt}
              accessibilityRole="button"
              onPress={() => send(prompt)}
              style={({ pressed }) => [
                styles.starter,
                { borderColor: t.border, backgroundColor: t.surface, opacity: pressed ? 0.7 : 1 },
              ]}
            >
              <Text style={{ ...type.caption, color: t.text }}>{prompt}</Text>
            </Pressable>
          ))}
        </View>
      )}

      <View style={[styles.composer, { borderColor: t.border, backgroundColor: t.surface }]}>
        {agents.length > 0 && (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Choose assistant"
            onPress={() => setPickingAgent(true)}
            style={{ paddingVertical: space.xs }}
          >
            <Text style={{ ...type.caption, color: t.brandText }}>{agent ?? "Assistant"} ▾</Text>
          </Pressable>
        )}

        <View style={styles.composerRow}>
          <TextInput
            accessibilityLabel="Message"
            value={draft}
            onChangeText={setDraft}
            placeholder={`Ask ${module.displayName}…`}
            placeholderTextColor={t.textMuted}
            multiline
            editable={!streaming}
            style={[styles.input, { borderColor: t.border, color: t.text, backgroundColor: t.background }]}
          />
          {streaming ? (
            <Button label="Stop" onPress={() => abort.current?.abort()} />
          ) : (
            <Button label="Send" tone="primary" disabled={draft.trim() === ""} onPress={() => send(draft)} />
          )}
        </View>
      </View>

      <Sheet open={pickingAgent} title="Assistant" onClose={() => setPickingAgent(false)}>
        {agents.map((a) => (
          <SheetOption
            key={a.name}
            label={a.name}
            detail={a.description}
            selected={a.name === agent}
            onPress={() => {
              setAgent(a.name);
              setPickingAgent(false);
            }}
          />
        ))}
      </Sheet>
    </KeyboardAvoidingView>
  );
}

/** The manifest (or a tenant's admin-created profile) can mark one agent as the default. */
function defaultAgentName(agents: ModuleAgent[] | undefined): string | null {
  return agents?.find((a) => a.isDefault)?.name ?? null;
}

function Bubble({ message, streaming }: { message: ChatMessage; streaming: boolean }) {
  const t = useResolvedTheme();
  const mine = message.role === "user";
  const waiting = !mine && message.text === "" && streaming;

  return (
    <View style={[styles.bubbleWrap, mine ? styles.mine : styles.theirs]}>
      <View
        style={[
          styles.bubble,
          {
            backgroundColor: mine ? t.brand : t.surface,
            borderColor: message.failed === true ? t.danger : t.border,
          },
        ]}
      >
        {waiting ? (
          <ActivityIndicator size="small" color={t.textMuted} />
        ) : (
          <Text
            style={{
              ...type.body,
              color: mine ? t.onBrand : message.failed === true ? t.danger : t.text,
            }}
          >
            {message.text}
          </Text>
        )}

        {/* Which tools ran. Not decoration: it is the visible edge of the audited tool call. */}
        {message.tools.length > 0 && (
          <View style={styles.chips}>
            {message.tools.map((tool, i) => (
              <View key={`${tool}-${i}`} style={[styles.chip, { borderColor: t.border }]}>
                <Text style={{ ...type.caption, color: t.textMuted }}>{tool}</Text>
              </View>
            ))}
          </View>
        )}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  transcript: { padding: space.lg, gap: space.sm, flexGrow: 1 },
  intro: { paddingVertical: space.xl },
  bubbleWrap: { flexDirection: "row" },
  mine: { justifyContent: "flex-end" },
  theirs: { justifyContent: "flex-start" },
  bubble: { maxWidth: "88%", borderWidth: 1, borderRadius: radius.lg, padding: space.md },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: space.xs, marginTop: space.sm },
  chip: { borderWidth: 1, borderRadius: radius.sm, paddingHorizontal: space.sm, paddingVertical: 2 },
  starters: { flexDirection: "row", flexWrap: "wrap", gap: space.sm, paddingHorizontal: space.lg },
  starter: { borderWidth: 1, borderRadius: radius.md, paddingHorizontal: space.md, paddingVertical: space.sm },
  composer: { borderTopWidth: 1, padding: space.md, gap: space.xs },
  composerRow: { flexDirection: "row", gap: space.sm, alignItems: "flex-end" },
  input: {
    flex: 1,
    minHeight: HIT_SIZE,
    maxHeight: 120,
    borderWidth: 1,
    borderRadius: radius.md,
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
    fontSize: 15,
  },
});
