import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { ChatPanel } from "./ChatPanel";
import { ConversationList } from "./ConversationList";
import type { ModuleAgent, ModuleSkill } from "../lib/api";

interface ChatViewProps {
  moduleId: string;
  suggestedPrompts?: string[];
  /** Selectable agents for this module's chat. */
  agents?: ModuleAgent[];
  /** Skills invocable from the composer with "/name". */
  skills?: ModuleSkill[];
  /** Streaming transport for the chat panel: "agui" (default, open AG-UI protocol) or "signalr". */
  transport?: "agui" | "signalr";
}

/**
 * The full chat experience for a module: a conversation-history sidebar plus the chat panel. Selecting a
 * conversation resumes it (the panel loads its messages); "New chat" starts a fresh one; and when a new
 * conversation is created it's selected and the list refreshed. Composed from the exported `ChatPanel` and
 * `ConversationList`, so a host can still use either alone.
 */
export function ChatView({ moduleId, suggestedPrompts, agents, skills, transport }: ChatViewProps) {
  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);
  const queryClient = useQueryClient();

  return (
    <div className="flex h-full min-h-0">
      <ConversationList
        moduleId={moduleId}
        selectedId={selectedId}
        onSelect={setSelectedId}
        onNew={() => setSelectedId(undefined)}
      />
      <div className="min-h-0 flex-1 pl-4">
        <ChatPanel
          moduleId={moduleId}
          transport={transport}
          suggestedPrompts={suggestedPrompts}
          agents={agents}
          skills={skills}
          conversationId={selectedId}
          onNewChat={() => setSelectedId(undefined)}
          onConversationStarted={(id) => {
            setSelectedId(id);
            void queryClient.invalidateQueries({ queryKey: ["conversations", moduleId] });
          }}
        />
      </div>
    </div>
  );
}
