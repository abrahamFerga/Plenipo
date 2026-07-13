using System.Net;
using System.Text;
using Cortex.Application.Channels;
using Cortex.AspNetCore.Channels;
using Cortex.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// Legal v1 item 9 — WhatsApp client intake for the legal vertical: with the channel bound to the
/// legal module, a client's document lands in the tenant file store and becomes a LEGAL agent turn
/// carrying the attachment reference, ready for the matter tools. Keyless: fake sender + media.
/// </summary>
[Collection("api")]
public sealed class WhatsAppLegalIntakeTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Client_document_on_whatsapp_becomes_a_legal_agent_turn_with_the_file_reference()
    {
        var outbox = new CapturingWhatsAppSender();
        var media = new FakeWhatsAppMediaClient();
        var mediaId = $"media-{Guid.NewGuid():N}";
        media.Media[mediaId] = ("the client's signed engagement letter"u8.ToArray(), "text/plain");

        // Same app, WhatsApp channel pointed at the LEGAL module — a per-tenant deployment choice.
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Channels:WhatsApp:Enabled", "true");
            builder.UseSetting("Channels:WhatsApp:VerifyToken", IntegrationFixture.WhatsAppVerifyToken);
            builder.UseSetting("Channels:WhatsApp:AppSecret", IntegrationFixture.WhatsAppAppSecret);
            builder.UseSetting("Channels:WhatsApp:AccessToken", "it-access-token");
            builder.UseSetting("Channels:WhatsApp:PhoneNumberId", "10000000002");
            builder.UseSetting("Channels:WhatsApp:ModuleId", "legal");
            builder.UseSetting("Channels:WhatsApp:TenantSlug", "dev");
            builder.UseSetting("Channels:WhatsApp:AllowUnknownSenders", "true");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IWhatsAppSender>(outbox);
                services.AddSingleton<IWhatsAppMediaClient>(media);
            });
        });

        var phone = "5215550142";
        var body = $$$"""
            {"object":"whatsapp_business_account","entry":[{"id":"waba-1","changes":[{"field":"messages",
            "value":{"messaging_product":"whatsapp",
            "contacts":[{"wa_id":"{{{phone}}}","profile":{"name":"Walk-in Client"}}],
            "messages":[{"id":"wamid.intake-{{{Guid.NewGuid():N}}}","from":"{{{phone}}}","type":"document",
            "document":{"id":"{{{mediaId}}}","mime_type":"text/plain","filename":"engagement-letter.txt",
            "caption":"Store this as part of the case of Julia Assange"}}]}}]}]}
            """;

        using var client = factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add(
            "X-Hub-Signature-256",
            WhatsAppSignature.Compute(Encoding.UTF8.GetBytes(body), IntegrationFixture.WhatsAppAppSecret));

        var response = await client.PostAsync("/api/channels/whatsapp/webhook", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The client got an answer, and the turn ran against the LEGAL module's agent.
        var reply = Assert.Single(outbox.Sent);
        Assert.Equal(phone, reply.To);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        var conversation = await db.Conversations.IgnoreQueryFilters()
            .Include(c => c.Messages)
            .FirstAsync(c => c.Id == WhatsAppChannelService.ConversationIdForPhone(tenant.Id, phone));
        Assert.Equal("legal", conversation.ModuleId);

        // The intake document is stored with WhatsApp provenance and referenced in the user turn —
        // everything attach_document_to_matter needs once the operator approves the agent's call.
        var stored = await db.StoredFiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.FileName == "engagement-letter.txt" && f.Source == "whatsapp");
        Assert.NotNull(stored);

        var userTurn = conversation.Messages.First(m => m.Role == Cortex.Core.Platform.MessageRole.User);
        Assert.Contains("Julia Assange", userTurn.Content);
        Assert.Contains($"file id: {stored!.Id}", userTurn.Content);
    }
}
