using System.ComponentModel;
using Plenipo.Infrastructure.Ai;
using Microsoft.Extensions.AI;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// Locks in the behavior the chatbot depends on when no real AI provider is configured: the mock client
/// must stream non-empty assistant text and report token usage, so the whole chat pipeline (streaming,
/// usage tracking, AG-UI) works out of the box.
/// </summary>
public sealed class MockChatClientTests
{
    [Description("Summarize spending.")]
    private static string SummarizeSpending() => "ok";

    [Description("Record a transaction.")]
    private static string RecordTransaction(string description, decimal amount) => $"recorded {description} {amount}";

    [Description("List tasks.")]
    private static string ListTasks() => "ok";

    [Description("Add a task.")]
    private static string AddTask(string title) => $"added {title}";

    private static readonly ChatMessage[] Conversation =
        [new(ChatRole.User, "How much did I spend on groceries?")];

    [Fact]
    public async Task Streaming_ProducesAssistantText_AndUsage()
    {
        var client = new MockChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(Conversation))
        {
            updates.Add(update);
        }

        var text = string.Concat(updates.Select(u => u.Text));
        Assert.False(string.IsNullOrWhiteSpace(text));
        // The reply echoes the user's question so it is visibly contextual.
        Assert.Contains("groceries", text, StringComparison.OrdinalIgnoreCase);

        var usage = updates
            .SelectMany(u => u.Contents)
            .OfType<UsageContent>()
            .Single()
            .Details;
        Assert.True(usage.TotalTokenCount > 0);
        Assert.True(usage.InputTokenCount > 0);
        Assert.True(usage.OutputTokenCount > 0);
    }

    [Fact]
    public async Task Streaming_ListsAvailableTools()
    {
        var client = new MockChatClient();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(SummarizeSpending)] };

        var text = string.Empty;
        await foreach (var update in client.GetStreamingResponseAsync(Conversation, options))
        {
            text += update.Text;
        }

        Assert.Contains(nameof(SummarizeSpending), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponse_ReturnsTextAndUsage()
    {
        var client = new MockChatClient();

        var response = await client.GetResponseAsync(Conversation);

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        Assert.NotNull(response.Usage);
        Assert.True(response.Usage!.TotalTokenCount > 0);
    }

    [Fact]
    public async Task Streaming_EmitsToolCall_WhenUserAsksToUseATool()
    {
        var client = new MockChatClient();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(SummarizeSpending)] };
        var messages = new[] { new ChatMessage(ChatRole.User, "Please use a tool to help me.") };

        var calls = new List<FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }

        // The mock actually drives a tool call through the real pipeline — not just listing names.
        var call = Assert.Single(calls);
        Assert.Equal(nameof(SummarizeSpending), call.Name);
    }

    [Fact]
    public async Task Streaming_SynthesizesRequiredArguments_FromToolSchema()
    {
        var client = new MockChatClient();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(RecordTransaction, name: "record_transaction")] };
        // "record" shares a token with the tool name, so it is selected.
        var messages = new[] { new ChatMessage(ChatRole.User, "record this for me") };

        FunctionCallContent? call = null;
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            call ??= update.Contents.OfType<FunctionCallContent>().FirstOrDefault();
        }

        Assert.NotNull(call);
        Assert.Equal("record_transaction", call!.Name);
        Assert.NotNull(call.Arguments);
        Assert.True(call.Arguments!.ContainsKey("description"));
        Assert.True(call.Arguments!.ContainsKey("amount"));
    }

    [Fact]
    public async Task Streaming_FillsRequiredNumberArgument_FromTheMessage()
    {
        var client = new MockChatClient();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(RecordTransaction, name: "record_transaction")] };
        var messages = new[] { new ChatMessage(ChatRole.User, "Record a 250 MXN dinner expense") };

        FunctionCallContent? call = null;
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            call ??= update.Contents.OfType<FunctionCallContent>().FirstOrDefault();
        }

        Assert.NotNull(call);
        // The first required string gets the message; the required number gets the number from it (not a 1 placeholder).
        Assert.Equal("Record a 250 MXN dinner expense", call!.Arguments!["description"]);
        Assert.Equal(250d, (double)call.Arguments!["amount"]!);
    }

    [Fact]
    public async Task Streaming_DistinguishesSimilarlyNamedTools_ByThePrompt()
    {
        var client = new MockChatClient();
        // The Tasks template's two tools share the "task"/"tasks" stem; the mock must still pick the right
        // one for each suggested prompt, or the build-a-module tutorial's "see it work" demo would call the
        // wrong tool. (list_tasks scores on "list"+"tasks"; add_task on "task" — singular vs plural separates them.)
        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(ListTasks, name: "list_tasks"),
                AIFunctionFactory.Create(AddTask, name: "add_task"),
            ],
        };

        Assert.Equal("list_tasks", await FirstToolCallNameAsync(client, options, "List my tasks"));
        Assert.Equal("add_task", await FirstToolCallNameAsync(client, options, "Add a task to buy groceries"));
    }

    private static async Task<string?> FirstToolCallNameAsync(MockChatClient client, ChatOptions options, string message)
    {
        var messages = new[] { new ChatMessage(ChatRole.User, message) };
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            var call = update.Contents.OfType<FunctionCallContent>().FirstOrDefault();
            if (call is not null)
            {
                return call.Name;
            }
        }

        return null;
    }

    [Fact]
    public async Task Streaming_SummarizesToolResult_WithoutCallingAnotherTool()
    {
        var client = new MockChatClient();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(SummarizeSpending)] };
        // History after a tool already ran this turn (what FunctionInvokingChatClient re-invokes us with).
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Please use a tool."),
            new(ChatRole.Assistant, [new FunctionCallContent("mock-SummarizeSpending", nameof(SummarizeSpending), null)]),
            new(ChatRole.Tool, [new FunctionResultContent("mock-SummarizeSpending", "You spent $42 on groceries.")]),
        };

        var calls = new List<FunctionCallContent>();
        var text = string.Empty;
        await foreach (var update in client.GetStreamingResponseAsync(history, options))
        {
            text += update.Text;
            calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }

        Assert.Empty(calls); // must not loop into a second tool call
        Assert.Contains(nameof(SummarizeSpending), text, StringComparison.Ordinal);
        Assert.Contains("groceries", text, StringComparison.OrdinalIgnoreCase); // echoes the tool's result
    }

    [Description("Log time on a matter.")]
    private static string LogTime(string matterName, double hours, string description) =>
        $"{matterName}|{hours}|{description}";

    [Description("Docket a deadline.")]
    private static string AddDeadline(string matterName, string title, string dueDate) =>
        $"{matterName}|{title}|{dueDate}";

    [Fact]
    public async Task Arguments_QuotedSpansFillStringParamsInOrder_NumbersAndDatesExtracted()
    {
        // The demo's explicit-argument syntax: quoted spans → string params in order, the first
        // number → the numeric param, the ISO date → the date-named param.
        var client = new MockChatClient();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(LogTime, name: "log_time")] };
        var call = await CallFor(client, options, "Log 0.5 hours of time on 'Vandelay acquisition' for the NDA call");

        Assert.Equal("Vandelay acquisition", call.Arguments!["matterName"]?.ToString());
        Assert.Equal(0.5, Assert.IsType<double>(call.Arguments["hours"]));
        // Only one quoted span: the remaining required string falls back to the full message.
        Assert.Contains("NDA call", call.Arguments["description"]?.ToString());

        options = new ChatOptions { Tools = [AIFunctionFactory.Create(AddDeadline, name: "add_deadline")] };
        call = await CallFor(client, options, "Add a deadline on 'Vandelay acquisition' titled 'Answer to complaint' due 2026-08-14");

        Assert.Equal("Vandelay acquisition", call.Arguments!["matterName"]?.ToString());
        Assert.Equal("Answer to complaint", call.Arguments["title"]?.ToString());
        Assert.Equal("2026-08-14", call.Arguments["dueDate"]?.ToString());
    }

    [Fact]
    public async Task Arguments_ContractionsAreNotQuotedSpans()
    {
        var client = new MockChatClient();
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(AddTask, name: "add_task")] };
        var call = await CallFor(client, options, "Add a task about the firm's playbook and the client's contract");

        // No phantom span between the two apostrophes — the full message falls through as before.
        Assert.Contains("firm's playbook", call.Arguments!["title"]?.ToString());
    }

    private static async Task<FunctionCallContent> CallFor(MockChatClient client, ChatOptions options, string message)
    {
        var calls = new List<FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, message)], options))
        {
            calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }

        return Assert.Single(calls);
    }
}
