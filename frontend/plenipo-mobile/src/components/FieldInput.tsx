import { useState } from "react";
import { Pressable, StyleSheet, Text, TextInput, View } from "react-native";
import { useQuery } from "@tanstack/react-query";
import { apiGet, type TabEditorField, type TabEditorOption } from "@plenipo/client";
import { HIT_SIZE, radius, space, type, useResolvedTheme } from "../theme";
import { Sheet, SheetOption } from "./ui";

/**
 * One form field of a server-declared shape, matching `@plenipo/ui`'s FieldInput decision for
 * decision — the same manifest must not behave differently on a phone.
 *
 * Fields whose valid values are KNOWN render a picker: a fixed vocabulary (`options`) or live
 * data (`optionsEndpoint` + `optionsField`, e.g. the household's account names). Everything else
 * is a text or numeric input; `multiline` grows, `masked` hides behind a reveal.
 *
 * The one adaptation is the picker itself: a web `<select>` becomes a bottom sheet, because a
 * native inline picker can't show labels for identifier-valued options the way this contract
 * needs (`America/Mexico_City` → "Mexico City").
 */

interface FieldInputProps {
  field: TabEditorField;
  value: string;
  disabled?: boolean;
  onChange: (value: string) => void;
}

export function FieldInput({ field, value, disabled, onChange }: FieldInputProps) {
  const t = useResolvedTheme();
  const [revealed, setRevealed] = useState(false);
  const [picking, setPicking] = useState(false);

  const dynamic = useQuery({
    queryKey: ["field-options", field.optionsEndpoint, field.optionsField],
    queryFn: () => apiGet<Record<string, unknown>[]>(field.optionsEndpoint!),
    enabled: Boolean(field.optionsEndpoint),
    staleTime: 30_000,
  });

  // Live options are values a human already chose (account names) — they read fine as their own
  // label. A declared vocabulary carries its own labels, because its values may be identifiers.
  const options: TabEditorOption[] | null =
    field.options ??
    (field.optionsEndpoint
      ? (dynamic.data ?? [])
          .map((row) => row[field.optionsField ?? "name"])
          .filter((v): v is string => typeof v === "string" && v.length > 0)
          .map((v) => ({ value: v, label: v }))
      : null);

  const inputStyle = [
    styles.input,
    { borderColor: t.border, backgroundColor: t.surface, color: t.text },
    disabled === true && { opacity: 0.6 },
  ];

  if (options !== null) {
    const empty = options.length === 0 && Boolean(field.optionsEndpoint);
    const chosen = options.find((o) => o.value === value);

    return (
      <View>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={field.label}
          accessibilityValue={{ text: chosen?.label ?? "Not chosen" }}
          accessibilityState={{ disabled: disabled === true || empty, expanded: picking }}
          disabled={disabled === true || empty}
          onPress={() => setPicking(true)}
          style={inputStyle}
        >
          {/* Never silently pre-pick: an unset field says so. A field that declares a default
              starts on it instead — chosen by the manifest, not guessed by the shell. */}
          <Text style={{ ...type.body, color: chosen ? t.text : t.textMuted }}>
            {chosen?.label ?? (empty ? "Nothing to choose yet" : "Choose…")}
          </Text>
        </Pressable>

        {empty && (
          <Text style={{ ...type.caption, color: t.textMuted, marginTop: space.xs }}>
            Nothing exists yet to pick from — add it first (or skip this for now).
          </Text>
        )}

        <Sheet open={picking} title={field.label} onClose={() => setPicking(false)}>
          {/* The blank entry is a real choice: clearing a field must be as reachable as setting it. */}
          <SheetOption
            label="Choose…"
            selected={value === ""}
            onPress={() => {
              onChange("");
              setPicking(false);
            }}
          />
          {options.map((option) => (
            <SheetOption
              key={option.value}
              label={option.label}
              selected={option.value === value}
              onPress={() => {
                onChange(option.value);
                setPicking(false);
              }}
            />
          ))}
        </Sheet>
      </View>
    );
  }

  if (field.multiline === true) {
    return (
      <TextInput
        accessibilityLabel={field.label}
        multiline
        numberOfLines={3}
        value={value}
        editable={disabled !== true}
        onChangeText={onChange}
        placeholderTextColor={t.textMuted}
        style={[inputStyle, styles.multiline]}
      />
    );
  }

  // Masked (PII-grade) fields type hidden behind an explicit reveal — the same intent as a masked
  // table column, applied while the value is being entered. Especially on a phone, which gets
  // read over shoulders far more often than a desktop does.
  if (field.masked === true && field.numeric !== true) {
    return (
      <View style={styles.row}>
        <TextInput
          accessibilityLabel={field.label}
          secureTextEntry={!revealed}
          autoCapitalize="none"
          autoCorrect={false}
          value={value}
          editable={disabled !== true}
          onChangeText={onChange}
          placeholderTextColor={t.textMuted}
          style={[inputStyle, { flex: 1 }]}
        />
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ selected: revealed }}
          accessibilityLabel={`${revealed ? "Hide" : "Reveal"} ${field.label}`}
          onPress={() => setRevealed((v) => !v)}
          style={[styles.revealButton, { borderColor: t.border }]}
        >
          <Text style={{ ...type.label, color: t.textMuted }}>{revealed ? "Hide" : "Show"}</Text>
        </Pressable>
      </View>
    );
  }

  return (
    <TextInput
      accessibilityLabel={field.label}
      value={value}
      editable={disabled !== true}
      onChangeText={onChange}
      // "decimal-pad" rather than "numeric": the contract allows negatives and fractions, and a
      // numeric pad on iOS offers neither a minus nor a separator.
      keyboardType={field.numeric === true ? "decimal-pad" : "default"}
      autoCapitalize={field.numeric === true ? "none" : "sentences"}
      placeholderTextColor={t.textMuted}
      style={inputStyle}
    />
  );
}

const styles = StyleSheet.create({
  input: {
    minHeight: HIT_SIZE,
    borderWidth: 1,
    borderRadius: radius.sm,
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
    fontSize: 15,
    justifyContent: "center",
  },
  multiline: { minHeight: HIT_SIZE * 2, textAlignVertical: "top" },
  row: { flexDirection: "row", gap: space.sm, alignItems: "stretch" },
  revealButton: {
    minWidth: 64,
    borderWidth: 1,
    borderRadius: radius.sm,
    alignItems: "center",
    justifyContent: "center",
  },
});
