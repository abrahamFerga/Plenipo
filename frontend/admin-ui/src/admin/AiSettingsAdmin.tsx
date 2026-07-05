import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@cortex/ui";

interface FormState {
  prompt: string;
  maxTokens: string;
  monthlyTokens: string;
  /** "" = deployment default provider. */
  provider: string;
  model: string;
  endpoint: string;
  /** New key to store (write-only); empty = keep whatever is on file. */
  apiKey: string;
  /** Explicitly remove the stored key. */
  clearKey: boolean;
}

const blankForm: FormState = {
  prompt: "",
  maxTokens: "",
  monthlyTokens: "",
  provider: "",
  model: "",
  endpoint: "",
  apiKey: "",
  clearKey: false,
};

/**
 * Per-tenant AI settings: the provider connection (switch provider/model at runtime, bring your
 * own API key — stored write-only in the secret vault), the assistant's base system prompt, and
 * the token budgets. A blank field falls back to the deployment default. The agent runner applies
 * all of this on the next chat turn. Requires platform.ai.manage.
 */
export function AiSettingsAdmin() {
  const settings = useQuery({ queryKey: ["admin", "ai-settings"], queryFn: api.admin.aiSettings });
  const queryClient = useQueryClient();
  const [form, setForm] = useState<FormState>(blankForm);
  const set = (patch: Partial<FormState>) => setForm((f) => ({ ...f, ...patch }));

  useEffect(() => {
    if (settings.data) {
      setForm({
        prompt: settings.data.systemPromptOverride ?? "",
        maxTokens: settings.data.maxConversationTokensOverride?.toString() ?? "",
        monthlyTokens: settings.data.maxMonthlyTokensOverride?.toString() ?? "",
        provider: settings.data.providerOverride ?? "",
        model: settings.data.modelOverride ?? "",
        endpoint: settings.data.endpointOverride ?? "",
        apiKey: "",
        clearKey: false,
      });
    }
  }, [settings.data]);

  const save = useMutation({
    mutationFn: (next: FormState) =>
      api.admin.setAiSettings({
        systemPrompt: next.prompt.trim() || null,
        maxConversationTokens: next.maxTokens.trim() === "" ? null : Number(next.maxTokens),
        maxMonthlyTokens: next.monthlyTokens.trim() === "" ? null : Number(next.monthlyTokens),
        provider: next.provider || null,
        model: next.model.trim() || null,
        endpoint: next.endpoint.trim() || null,
        // Write-only contract: null keeps the stored key, "" clears it, non-empty replaces it.
        apiKey: next.clearKey ? "" : next.apiKey.trim() || null,
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["admin", "ai-settings"] }),
  });

  if (settings.isLoading) {
    return <p className="text-sm text-slate-500">Loading AI settings…</p>;
  }
  if (settings.isError) {
    return (
      <p className="text-sm text-red-600">
        Could not load AI settings — this view requires the platform.ai.manage permission.
      </p>
    );
  }

  const data = settings.data!;
  const { prompt, maxTokens, monthlyTokens, provider, model, endpoint, apiKey, clearKey } = form;
  const tokensInvalid = maxTokens.trim() !== "" && (!/^\d+$/.test(maxTokens.trim()) || Number(maxTokens) < 0);
  const monthlyInvalid =
    monthlyTokens.trim() !== "" && (!/^\d+$/.test(monthlyTokens.trim()) || Number(monthlyTokens) < 0);
  const needsEndpoint = provider === "AzureOpenAI" || provider === "Ollama";
  const needsKey = provider === "OpenAI" || provider === "Anthropic";
  const willHaveKey = apiKey.trim() !== "" || (data.hasApiKey && !clearKey);
  const keyMissing = needsKey && !willHaveKey;

  return (
    <div className="max-w-2xl space-y-5">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">AI Settings</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Customize the assistant for this tenant. Leave a field blank to use the deployment default. Changes
          take effect on the next chat turn and are recorded in the audit trail.
        </p>
      </header>

      <form
        className="space-y-5"
        onSubmit={(e) => {
          e.preventDefault();
          if (!tokensInvalid && !monthlyInvalid && !keyMissing) save.mutate(form);
        }}
      >
        <fieldset className="space-y-3 rounded-lg border border-slate-200 p-4 dark:border-slate-700">
          <legend className="px-1 text-sm font-semibold text-slate-700 dark:text-slate-200">
            Provider &amp; model
          </legend>
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1">
              <label htmlFor="ai-provider" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
                Provider
              </label>
              <select
                id="ai-provider"
                value={provider}
                onChange={(e) => set({ provider: e.target.value })}
                className="w-full rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
              >
                <option value="">Deployment default ({data.defaultProvider})</option>
                <option value="Mock">Mock (keyless demo)</option>
                <option value="OpenAI">OpenAI</option>
                <option value="AzureOpenAI">Azure OpenAI</option>
                <option value="Anthropic">Anthropic (Claude)</option>
                <option value="Ollama">Ollama</option>
                <option value="None">None (disable chat)</option>
              </select>
            </div>
            <div className="space-y-1">
              <label htmlFor="ai-model" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
                Model
              </label>
              <input
                id="ai-model"
                value={model}
                onChange={(e) => set({ model: e.target.value })}
                placeholder={`Default: ${data.defaultModel}`}
                className="w-full rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
              />
            </div>
          </div>
          {needsEndpoint && (
            <div className="space-y-1">
              <label htmlFor="ai-endpoint" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
                Endpoint
              </label>
              <input
                id="ai-endpoint"
                value={endpoint}
                onChange={(e) => set({ endpoint: e.target.value })}
                placeholder={provider === "Ollama" ? "http://localhost:11434/v1" : "https://your-resource.openai.azure.com"}
                className="w-full rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
              />
            </div>
          )}
          {provider !== "" && provider !== "Mock" && provider !== "None" && (
            <div className="space-y-1">
              <label htmlFor="ai-api-key" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
                API key
              </label>
              <input
                id="ai-api-key"
                type="password"
                autoComplete="off"
                value={apiKey}
                onChange={(e) => set({ apiKey: e.target.value, clearKey: false })}
                placeholder={data.hasApiKey ? "A key is on file — enter a new one to replace it" : "sk-…"}
                className="w-full rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
              />
              <p className="text-xs text-slate-400">
                Stored write-only in the secret vault — it is never shown again, only replaced or cleared.
              </p>
              {data.hasApiKey && (
                <label className="flex items-center gap-1.5 text-xs text-slate-500 dark:text-slate-400">
                  <input
                    type="checkbox"
                    checked={clearKey}
                    onChange={(e) => set({ clearKey: e.target.checked, apiKey: "" })}
                  />
                  Clear the stored key
                </label>
              )}
              {keyMissing && <p className="text-xs text-red-600">The {provider} provider requires an API key.</p>}
            </div>
          )}
          <p className="text-xs text-slate-400">
            Switching applies on the next chat turn — token usage is attributed to this tenant's provider and
            model. Individual agents can pin their own model under Agent Profiles.
          </p>
        </fieldset>
        <div className="space-y-1">
          <label htmlFor="system-prompt" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
            System prompt
          </label>
          <textarea
            id="system-prompt"
            value={prompt}
            onChange={(e) => set({ prompt: e.target.value })}
            rows={5}
            placeholder={`Default: ${data.defaultSystemPrompt}`}
            className="w-full rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
          />
          <p className="text-xs text-slate-400">Blank uses the deployment default. Module instructions are still appended per conversation.</p>
        </div>

        <div className="space-y-1">
          <label htmlFor="max-tokens" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
            Conversation token budget
          </label>
          <input
            id="max-tokens"
            value={maxTokens}
            onChange={(e) => set({ maxTokens: e.target.value })}
            inputMode="numeric"
            placeholder={`Default: ${data.defaultMaxConversationTokens === 0 ? "unlimited" : data.defaultMaxConversationTokens}`}
            className="w-48 rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
          />
          <p className="text-xs text-slate-400">
            Max tokens a single conversation may consume before further turns are refused. 0 = unlimited; blank = default.
          </p>
          {tokensInvalid && <p className="text-xs text-red-600">Enter a non-negative whole number.</p>}
        </div>

        <div className="space-y-1">
          <label htmlFor="monthly-tokens" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
            Monthly token budget (organization-wide)
          </label>
          <input
            id="monthly-tokens"
            value={monthlyTokens}
            onChange={(e) => set({ monthlyTokens: e.target.value })}
            inputMode="numeric"
            placeholder={`Default: ${data.defaultMaxMonthlyTokens === 0 ? "unlimited" : data.defaultMaxMonthlyTokens}`}
            className="w-48 rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
          />
          <p className="text-xs text-slate-400">
            Total tokens the whole tenant may consume per calendar month (UTC). Chat is refused once reached;
            admins are notified at 80% and at exhaustion. 0 = unlimited; blank = default.
          </p>
          {monthlyInvalid && <p className="text-xs text-red-600">Enter a non-negative whole number.</p>}
        </div>

        {save.isError && <p className="text-xs text-red-600">{(save.error as Error).message}</p>}

        <div className="flex items-center gap-2">
          <button
            type="submit"
            disabled={tokensInvalid || monthlyInvalid || keyMissing || save.isPending}
            className="focus-ring rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500 disabled:opacity-40"
          >
            {save.isPending ? "Saving…" : "Save"}
          </button>
          <button
            type="button"
            disabled={
              save.isPending ||
              (prompt === "" && maxTokens === "" && monthlyTokens === "" && provider === "" && model === "" && !data.hasApiKey)
            }
            onClick={() => {
              // Back to the deployment defaults, including clearing any stored tenant key.
              const reset = { ...blankForm, clearKey: true };
              setForm(reset);
              save.mutate(reset);
            }}
            className="focus-ring rounded border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-600 disabled:opacity-40 dark:border-slate-600 dark:text-slate-300"
          >
            Reset to defaults
          </button>
        </div>
      </form>
    </div>
  );
}
