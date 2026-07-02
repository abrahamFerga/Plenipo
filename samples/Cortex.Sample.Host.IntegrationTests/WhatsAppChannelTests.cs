using System.Net;
using System.Text;
using Cortex.AspNetCore.Channels;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// End-to-end tests for the WhatsApp channel, run with zero external dependencies: the Mock AI
/// provider answers the agent turn and a capturing fake stands in for the Meta Cloud API sender. The
/// webhook is exercised exactly as Meta calls it — anonymous HTTP with an HMAC signature over the raw
/// body — so signature verification, JIT user provisioning, the authorized agent runner, conversation
/// persistence, and the outbound reply are all covered by the real pipeline.
/// </summary>
[Collection("api")]
public sealed class WhatsAppChannelTests(IntegrationFixture fixture)
{
    private const string Phone = "5215550100";

    [Fact]
    public async Task Verification_handshake_echoes_challenge_for_correct_token()
    {
        using var client = fixture.WhatsAppFactory.CreateClient();

        var response = await client.GetAsync(
            $"/api/channels/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={IntegrationFixture.WhatsAppVerifyToken}&hub.challenge=challenge-123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("challenge-123", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Verification_handshake_rejects_wrong_token()
    {
        using var client = fixture.WhatsAppFactory.CreateClient();

        var response = await client.GetAsync(
            "/api/channels/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=x");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_is_disabled_by_default()
    {
        // The base factory has no Channels:WhatsApp config, so the endpoints must not exist.
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(
            "/api/channels/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=x&hub.challenge=x");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_rejects_missing_or_tampered_signature()
    {
        using var client = fixture.WhatsAppFactory.CreateClient();
        var body = InboundText("wamid.sig-test", "5215550999", "hello");

        using var unsigned = new StringContent(body, Encoding.UTF8, "application/json");
        var noSignature = await client.PostAsync("/api/channels/whatsapp/webhook", unsigned);
        Assert.Equal(HttpStatusCode.Unauthorized, noSignature.StatusCode);

        using var tampered = new StringContent(body, Encoding.UTF8, "application/json");
        tampered.Headers.Add("X-Hub-Signature-256", WhatsAppSignature.Compute(Encoding.UTF8.GetBytes(body + " "), IntegrationFixture.WhatsAppAppSecret));
        var badSignature = await client.PostAsync("/api/channels/whatsapp/webhook", tampered);
        Assert.Equal(HttpStatusCode.Unauthorized, badSignature.StatusCode);
    }

    [Fact]
    public async Task Text_message_runs_an_agent_turn_and_replies_over_whatsapp()
    {
        var before = fixture.WhatsAppOutbox.Sent.Count;

        var response = await PostSignedAsync(InboundText($"wamid.turn-{Guid.NewGuid():N}", Phone, "How much did I spend on groceries?"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = fixture.WhatsAppOutbox.Sent.Skip(before).ToList();
        var reply = Assert.Single(sent);
        Assert.Equal(Phone, reply.To);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));

        // The turn is a real, persisted conversation for the JIT-provisioned whatsapp:{phone} user.
        using var scope = fixture.WhatsAppFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Subject == $"whatsapp:{Phone}");
        Assert.NotNull(user);

        var conversation = await fixture.GetConversationAsync(
            WhatsAppChannelService.ConversationIdForPhone(tenant.Id, Phone));
        Assert.Equal(user!.Id, conversation.UserId);
        Assert.Contains(conversation.Messages, m => m.Content.Contains("groceries", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Consecutive_messages_continue_one_conversation_per_phone()
    {
        var phone = "5215550177";
        await PostSignedAsync(InboundText($"wamid.conv1-{Guid.NewGuid():N}", phone, "What did I spend on dining?"));
        await PostSignedAsync(InboundText($"wamid.conv2-{Guid.NewGuid():N}", phone, "And on groceries?"));

        using var scope = fixture.WhatsAppFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");

        var conversation = await fixture.GetConversationAsync(
            WhatsAppChannelService.ConversationIdForPhone(tenant.Id, phone));

        // Two user turns + two assistant replies accumulated in a single conversation row.
        Assert.True(conversation.Messages.Count >= 4);

        // The MAF AgentSession must survive the Postgres jsonb round-trip: turn 1 serializes it, and
        // turn 2 resumes it (jsonb reorders JSON keys, so the polymorphic $type discriminator comes
        // back out of position — a deserializer without out-of-order tolerance fails the whole turn).
        Assert.False(string.IsNullOrEmpty(conversation.SessionState));

        // And turn 2 must be a real agent reply, not the channel's error fallback.
        var lastAssistant = conversation.Messages
            .Where(m => m.Role == MessageRole.Assistant)
            .OrderBy(m => m.CreatedAt)
            .Last();
        Assert.DoesNotContain("couldn't process", lastAssistant.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicate_delivery_of_same_message_id_is_processed_once()
    {
        var before = fixture.WhatsAppOutbox.Sent.Count;
        var body = InboundText($"wamid.dup-{Guid.NewGuid():N}", Phone, "Am I over budget on anything?");

        await PostSignedAsync(body);
        await PostSignedAsync(body); // Meta redelivers on slow/failed responses

        Assert.Equal(before + 1, fixture.WhatsAppOutbox.Sent.Count);
    }

    [Fact]
    public async Task Status_only_delivery_is_acknowledged_without_replying()
    {
        var before = fixture.WhatsAppOutbox.Sent.Count;
        const string body = """
            {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages",
            "value":{"messaging_product":"whatsapp","statuses":[{"id":"wamid.x","status":"delivered"}]}}]}]}
            """;

        var response = await PostSignedAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, fixture.WhatsAppOutbox.Sent.Count);
    }

    [Fact]
    public async Task Non_text_message_gets_a_text_only_notice()
    {
        var before = fixture.WhatsAppOutbox.Sent.Count;
        var body = $$$"""
            {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages",
            "value":{"messaging_product":"whatsapp",
            "contacts":[{"wa_id":"5215550188","profile":{"name":"Imagen"}}],
            "messages":[{"id":"wamid.img-{{{Guid.NewGuid():N}}}","from":"5215550188","type":"image"}]}}]}]}
            """;

        await PostSignedAsync(body);

        var sent = fixture.WhatsAppOutbox.Sent.Skip(before).ToList();
        var notice = Assert.Single(sent);
        Assert.Equal("5215550188", notice.To);
        Assert.Contains("text", notice.Text, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> PostSignedAsync(string body)
    {
        using var client = fixture.WhatsAppFactory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add(
            "X-Hub-Signature-256",
            WhatsAppSignature.Compute(Encoding.UTF8.GetBytes(body), IntegrationFixture.WhatsAppAppSecret));
        return await client.PostAsync("/api/channels/whatsapp/webhook", content);
    }

    private static string InboundText(string messageId, string from, string text) => $$$"""
        {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages",
        "value":{"messaging_product":"whatsapp",
        "contacts":[{"wa_id":"{{{from}}}","profile":{"name":"Integration Tester"}}],
        "messages":[{"id":"{{{messageId}}}","from":"{{{from}}}","type":"text","text":{"body":"{{{text}}}"}}]}}]}]}
        """;
}
