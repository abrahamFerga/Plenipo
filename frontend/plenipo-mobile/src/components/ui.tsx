import type { ReactNode } from "react";
import {
  ActivityIndicator,
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
  type StyleProp,
  type ViewStyle,
} from "react-native";
import { HIT_SIZE, radius, space, type, useResolvedTheme, type PlenipoTheme } from "../theme";

/**
 * The shell's handful of primitives. Small on purpose: everything here exists because more than
 * one server-driven screen needs it, and a product that wants richer chrome registers its own
 * screen rather than growing this file.
 */

export function Loading({ label = "Loading…" }: { label?: string }) {
  const t = useResolvedTheme();
  return (
    <View style={[styles.center, { padding: space.xl }]}>
      <ActivityIndicator color={t.brand} />
      <Text style={{ ...type.caption, color: t.textMuted, marginTop: space.sm }}>{label}</Text>
    </View>
  );
}

/**
 * An error the user can actually act on. Errors are never swallowed into an empty state — a tab
 * that failed to load must not look like a tab with no data.
 */
export function ErrorNote({ error }: { error: unknown }) {
  const t = useResolvedTheme();
  const message = error instanceof Error ? error.message : String(error);
  return (
    // `accessible` groups the note into one focusable element — without it the role is inert and
    // a screen reader walks past the failure instead of announcing it.
    <View
      accessible
      accessibilityRole="alert"
      accessibilityLabel={message}
      style={[styles.note, { borderColor: t.danger, backgroundColor: t.surface }]}
    >
      <Text style={{ ...type.body, color: t.danger }}>{message}</Text>
    </View>
  );
}

/** A neutral or failed outcome message from an action — what happened, stated plainly. */
export function OutcomeNote({ message, tone = "neutral" }: { message: string; tone?: "neutral" | "error" }) {
  const t = useResolvedTheme();
  return (
    <View
      accessible
      accessibilityRole="alert"
      accessibilityLabel={message}
      style={[
        styles.note,
        { borderColor: tone === "error" ? t.danger : t.border, backgroundColor: t.surfaceMuted },
      ]}
    >
      <Text style={{ ...type.body, color: tone === "error" ? t.danger : t.text }}>{message}</Text>
    </View>
  );
}

/** The "nothing here" state, which a tab's `placeholder` gets to word. */
export function EmptyState({ text }: { text: string }) {
  const t = useResolvedTheme();
  return (
    <View style={[styles.empty, { borderColor: t.border }]}>
      <Text style={{ ...type.body, color: t.textMuted, textAlign: "center" }}>{text}</Text>
    </View>
  );
}

export type ButtonTone = "primary" | "secondary" | "danger";

export function Button({
  label,
  onPress,
  tone = "secondary",
  disabled,
  busy,
  style,
}: {
  label: string;
  onPress: () => void;
  tone?: ButtonTone;
  disabled?: boolean;
  busy?: boolean;
  style?: StyleProp<ViewStyle>;
}) {
  const t = useResolvedTheme();
  const palette = buttonPalette(t, tone);
  const isDisabled = disabled === true || busy === true;

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled: isDisabled, busy }}
      disabled={isDisabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.button,
        {
          backgroundColor: palette.background,
          borderColor: palette.border,
          opacity: isDisabled ? 0.45 : pressed ? 0.8 : 1,
        },
        style,
      ]}
    >
      {busy === true && <ActivityIndicator size="small" color={palette.text} style={{ marginRight: space.sm }} />}
      <Text style={{ ...type.label, color: palette.text }}>{label}</Text>
    </Pressable>
  );
}

function buttonPalette(t: PlenipoTheme, tone: ButtonTone) {
  switch (tone) {
    case "primary":
      return { background: t.brand, border: t.brand, text: t.onBrand };
    case "danger":
      return { background: "transparent", border: t.danger, text: t.danger };
    default:
      return { background: "transparent", border: t.border, text: t.text };
  }
}

/** A raised container — the unit a row, a form, or a message sits in. */
export function Card({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  const t = useResolvedTheme();
  return (
    <View style={[styles.card, { backgroundColor: t.surface, borderColor: t.border }, style]}>{children}</View>
  );
}

/**
 * A confirmation for a consequential action. The manifest decides WHEN one appears (a
 * `TabAction.confirm` / `TabRowAction.confirm` string), so this only has to render it.
 */
export function ConfirmDialog({
  open,
  title,
  body,
  confirmLabel,
  tone = "primary",
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: string;
  body: string;
  confirmLabel: string;
  tone?: ButtonTone;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const t = useResolvedTheme();
  if (!open) return null;

  return (
    <Modal transparent animationType="fade" visible onRequestClose={onCancel}>
      <View style={styles.backdrop}>
        <View style={[styles.dialog, { backgroundColor: t.surface, borderColor: t.border }]}>
          <Text style={{ ...type.heading, color: t.text }}>{title}</Text>
          {body !== "" && (
            <Text style={{ ...type.body, color: t.textMuted, marginTop: space.sm }}>{body}</Text>
          )}
          <View style={styles.dialogActions}>
            <Button label="Cancel" onPress={onCancel} />
            <Button label={confirmLabel} tone={tone} onPress={onConfirm} />
          </View>
        </View>
      </View>
    </Modal>
  );
}

/** A bottom sheet for pickers (modules, agents). Simpler than a nav screen for a transient list. */
export function Sheet({
  open,
  title,
  onClose,
  children,
}: {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
}) {
  const t = useResolvedTheme();
  if (!open) return null;

  return (
    <Modal transparent animationType="slide" visible onRequestClose={onClose}>
      <Pressable style={styles.backdrop} onPress={onClose} accessibilityLabel={`Close ${title}`}>
        {/* Swallow taps inside the sheet so only the backdrop dismisses. */}
        <Pressable
          onPress={() => {}}
          style={[styles.sheet, { backgroundColor: t.surface, borderColor: t.border }]}
        >
          <Text style={{ ...type.heading, color: t.text, marginBottom: space.md }}>{title}</Text>
          <ScrollView>{children}</ScrollView>
        </Pressable>
      </Pressable>
    </Modal>
  );
}

/** A tappable row inside a {@link Sheet}. */
export function SheetOption({
  label,
  detail,
  selected,
  onPress,
}: {
  label: string;
  detail?: string | null;
  selected?: boolean;
  onPress: () => void;
}) {
  const t = useResolvedTheme();
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={({ pressed }) => [
        styles.sheetOption,
        { borderColor: t.border, opacity: pressed ? 0.7 : 1 },
        selected === true && { backgroundColor: t.surfaceMuted },
      ]}
    >
      <View style={{ flex: 1 }}>
        <Text style={{ ...type.body, color: t.text, fontWeight: selected === true ? "600" : "400" }}>
          {label}
        </Text>
        {detail != null && detail !== "" && (
          <Text style={{ ...type.caption, color: t.textMuted, marginTop: 2 }}>{detail}</Text>
        )}
      </View>
      {/* Selection is never color-only: a check mark carries it too. */}
      {selected === true && <Text style={{ ...type.body, color: t.brandText }}>✓</Text>}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  center: { alignItems: "center", justifyContent: "center" },
  note: { borderWidth: 1, borderRadius: radius.md, padding: space.md },
  empty: {
    borderWidth: 1,
    borderStyle: "dashed",
    borderRadius: radius.md,
    padding: space.xl,
    alignItems: "center",
  },
  button: {
    minHeight: HIT_SIZE,
    paddingHorizontal: space.lg,
    borderRadius: radius.md,
    borderWidth: 1,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
  },
  card: { borderWidth: 1, borderRadius: radius.md, padding: space.md },
  backdrop: { flex: 1, backgroundColor: "rgba(15,23,42,0.55)", justifyContent: "flex-end" },
  dialog: {
    margin: space.lg,
    padding: space.lg,
    borderRadius: radius.lg,
    borderWidth: 1,
    marginBottom: space.xl * 2,
  },
  dialogActions: { flexDirection: "row", gap: space.sm, justifyContent: "flex-end", marginTop: space.lg },
  sheet: {
    borderTopLeftRadius: radius.lg,
    borderTopRightRadius: radius.lg,
    borderWidth: 1,
    padding: space.lg,
    maxHeight: "70%",
  },
  sheetOption: {
    minHeight: HIT_SIZE,
    flexDirection: "row",
    alignItems: "center",
    gap: space.md,
    paddingVertical: space.md,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
});
