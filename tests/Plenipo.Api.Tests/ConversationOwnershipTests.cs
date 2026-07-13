using System.Net;
using System.Net.Http.Json;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of conversation ownership — horizontal access control WITHIN a tenant. The chat history
/// endpoints are owner-scoped (<c>c.UserId == current.UserId</c>), so one user must not be able to read, rename,
/// or delete another user's conversation even though both are authorized chat users in the same tenant. A leak
/// here would be a cross-user data breach, so it's worth pinning end-to-end.
/// </summary>
public sealed class ConversationOwnershipTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public ConversationOwnershipTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "user"); // has chat.conversations.view, not admin
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task A_user_cannot_read_rename_or_delete_another_users_conversation()
    {
        // Alice signs in (provisioning her user) so we can own a conversation as her.
        var alice = ClientAs("owner-alice");
        var aliceMe = await (await alice.GetAsync("/api/platform/me")).Content.ReadFromJsonAsync<MeDto>();
        Assert.NotNull(aliceMe?.userId);
        Assert.NotNull(aliceMe?.tenantId);

        var conversationId = SeedConversation(
            tenantId: Guid.Parse(aliceMe!.tenantId!),
            userId: Guid.Parse(aliceMe.userId!));

        // Bob — a different, authorized chat user in the SAME tenant — must be walled off (404, not 403: he is
        // permitted to use chat, he just isn't the owner).
        var bob = ClientAs("intruder-bob");
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/chat/conversations/{conversationId}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.PutAsJsonAsync($"/api/chat/conversations/{conversationId}/title", new { title = "hijacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.DeleteAsync($"/api/chat/conversations/{conversationId}")).StatusCode);

        // Alice, the owner, can rename her own conversation (positive control).
        Assert.Equal(HttpStatusCode.NoContent,
            (await alice.PutAsJsonAsync($"/api/chat/conversations/{conversationId}/title", new { title = "My chat" })).StatusCode);
    }

    private Guid SeedConversation(Guid tenantId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var conversation = new Conversation
        {
            TenantId = tenantId,
            UserId = userId,
            ModuleId = "test-module",
            Title = "Alice's chat",
        };
        db.Conversations.Add(conversation);
        db.SaveChanges();
        return conversation.Id;
    }

    private sealed record MeDto(string? userId, string? displayName, string? tenantId, string[] permissions);
}
