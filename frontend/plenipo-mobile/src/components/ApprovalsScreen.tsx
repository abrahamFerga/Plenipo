import { useState } from "react";
import { RefreshControl, ScrollView, StyleSheet, Text, View } from "react-native";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, type ApprovalResolution, type PendingApproval } from "@plenipo/client";
import { radius, space, type, useResolvedTheme } from "../theme";
import { Button, Card, ConfirmDialog, EmptyState, ErrorNote, Loading, OutcomeNote } from "./ui";

/**
 * Human-in-the-loop approvals: the side-effecting tool calls the agent was blocked from running
 * on its own.
 *
 * This is the screen that most justifies a mobile shell. Approval is the platform's deliberate
 * bottleneck — a write does not happen until an authorized human says so — and a bottleneck is
 * only as good as how fast the human can reach it. A push notification plus this screen turns
 * "blocked until someone opens a laptop" into "blocked for ninety seconds".
 *
 * Approving EXECUTES the recorded call server-side and returns what actually happened, which is
 * shown here rather than swallowed: an approval that then failed must never read as a success.
 */
export function ApprovalsScreen() {
  const t = useResolvedTheme();
  const qc = useQueryClient();
  const [outcome, setOutcome] = useState<{ message: string; failed: boolean } | null>(null);
  const [confirming, setConfirming] = useState<{ approval: PendingApproval; decision: "approve" | "reject" } | null>(
    null,
  );

  const { data, isLoading, isError, error, refetch, isRefetching } = useQuery({
    queryKey: ["approvals"],
    queryFn: () => api.approvals.list(),
    // The badge has to be roughly live: a request parked while you were on another screen should
    // show up without a manual pull.
    refetchInterval: 30_000,
  });

  const resolve = useMutation({
    mutationFn: ({ approval, decision }: { approval: PendingApproval; decision: "approve" | "reject" }) =>
      decision === "approve" ? api.approvals.approve(approval.id) : api.approvals.reject(approval.id),
    onSuccess: (resolution: ApprovalResolution) => {
      setOutcome({
        // The server composes the wording ("✅ Approved by … — 'tool' ran. …"); echoing its own
        // sentence keeps the phone and the chat transcript telling the same story.
        message: resolution.note ?? resolution.result ?? `${resolution.status}.`,
        failed: false,
      });
      void qc.invalidateQueries({ queryKey: ["approvals"] });
      // An executed tool almost always changed data some tab is showing.
      void qc.invalidateQueries({ queryKey: ["tab-data"] });
    },
    onError: (e) => setOutcome({ message: (e as Error).message, failed: true }),
  });

  if (isLoading) return <Loading />;
  if (isError) {
    return (
      <ScrollView contentContainerStyle={styles.page}>
        <ErrorNote error={error} />
      </ScrollView>
    );
  }

  const approvals = data ?? [];

  return (
    <>
      <ScrollView
        contentContainerStyle={styles.page}
        refreshControl={<RefreshControl refreshing={isRefetching} onRefresh={() => void refetch()} />}
      >
        {outcome != null && (
          <View style={{ marginBottom: space.md }}>
            <OutcomeNote message={outcome.message} tone={outcome.failed ? "error" : "neutral"} />
          </View>
        )}

        {approvals.length === 0 ? (
          <EmptyState text="Nothing waiting on you. Tool calls that change data will appear here for approval." />
        ) : (
          approvals.map((approval) => (
            <Card key={approval.id} style={{ marginBottom: space.sm }}>
              <Text style={{ ...type.heading, color: t.text }}>
                {approval.description ?? approval.toolName}
              </Text>
              <Text style={{ ...type.caption, color: t.textMuted, marginTop: 2 }}>
                {approval.moduleId} · {approval.toolName}
                {approval.userDisplay != null ? ` · requested by ${approval.userDisplay}` : ""}
              </Text>

              {/* The arguments are the whole point of review: approving without seeing what will
                  run is a rubber stamp. Shown verbatim, never summarized. */}
              {approval.argumentsJson != null && approval.argumentsJson !== "" && (
                <View style={[styles.args, { backgroundColor: t.surfaceMuted, borderColor: t.border }]}>
                  <Text style={{ ...type.caption, color: t.text, fontFamily: monospace }}>
                    {prettyJson(approval.argumentsJson)}
                  </Text>
                </View>
              )}

              <View style={styles.actions}>
                <Button
                  label="Approve"
                  tone="primary"
                  busy={resolve.isPending}
                  onPress={() =>
                    // "low" risk is a one-tap confirm by the tool's own declaration; anything else
                    // gets a second step. The hint shapes ceremony, never permission.
                    approval.risk === "low"
                      ? resolve.mutate({ approval, decision: "approve" })
                      : setConfirming({ approval, decision: "approve" })
                  }
                />
                <Button
                  label="Reject"
                  tone="danger"
                  busy={resolve.isPending}
                  onPress={() => resolve.mutate({ approval, decision: "reject" })}
                />
              </View>
            </Card>
          ))
        )}
      </ScrollView>

      <ConfirmDialog
        open={confirming !== null}
        title={`Run ${confirming?.approval.toolName ?? ""}?`}
        body="This executes the recorded call now, exactly as shown. It changes data."
        confirmLabel="Approve and run"
        onConfirm={() => {
          if (confirming) resolve.mutate(confirming);
          setConfirming(null);
        }}
        onCancel={() => setConfirming(null)}
      />
    </>
  );
}

const monospace = "monospace";

/** Pretty-print the recorded arguments, falling back to the raw text if it isn't JSON. */
function prettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

const styles = StyleSheet.create({
  page: { padding: space.lg, paddingBottom: space.xl * 2 },
  args: {
    marginTop: space.md,
    padding: space.sm,
    borderWidth: 1,
    borderRadius: radius.sm,
  },
  actions: { flexDirection: "row", gap: space.sm, marginTop: space.md },
});
