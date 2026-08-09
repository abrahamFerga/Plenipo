using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The approvals queue must name WHO proposed each parked write. This is the conformance test for
/// platform-request #116 (auditworthy): the approver is accountable for what they sign off, and
/// <c>UserDisplay</c> cannot carry that — it is a best-effort label, two people can share one, and
/// under dev-auth every subject displays as "Dev User", so it distinguishes nobody. The DTO has to
/// carry the stable <c>UserId</c> the model already stores.
///
/// The requester's acceptance test, run as written: park as one subject, read as another, assert the
/// entry names the proposer and not the reader — then park as a third and assert the two entries
/// differ, so the field is genuinely per-proposer rather than a constant. All three subjects hold
/// <c>system_admin</c> here because the point under test is the projection; that a proposer need not
/// hold <c>chat.approvals.manage</c> is already pinned by <see cref="ApprovalsSecurityTests"/>.
/// </summary>
public sealed class ApprovalProposerIdentityTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public ApprovalProposerIdentityTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    private static async Task<Guid> ParkAnApprovalAsync(HttpClient client)
    {
        // The `record` tool is RequiresApproval, so the turn blocks and writes a pending row rather
        // than executing. The proposer's identity is captured at that moment.
        var response = await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "please use the record tool" });
        response.EnsureSuccessStatusCode();

        var me = await client.GetFromJsonAsync<MeDto>("/api/platform/me");
        return me!.UserId!.Value;
    }

    [Fact]
    public async Task The_queue_names_the_proposer_and_not_the_reader()
    {
        var annaId = await ParkAnApprovalAsync(ClientAs("proposer-anna"));
        var carlId = await ParkAnApprovalAsync(ClientAs("proposer-carl"));

        var approver = ClientAs("approver-bea");
        var beaId = (await approver.GetFromJsonAsync<MeDto>("/api/platform/me"))!.UserId!.Value;

        var queue = (await approver.GetFromJsonAsync<List<ApprovalDto>>("/api/chat/approvals"))!;
        var proposers = queue.Where(a => a.ToolName == "record").Select(a => a.UserId).ToList();

        // Each proposer is named...
        Assert.Contains(annaId, proposers);
        Assert.Contains(carlId, proposers);

        // ...the field is per-proposer rather than a constant...
        Assert.NotEqual(annaId, carlId);

        // ...and reading the queue does not make the reader the proposer.
        Assert.DoesNotContain(beaId, proposers);

        // The count increments by exactly one per parked write — an entry attributed twice is as
        // misleading as one attributed to nobody.
        Assert.Equal(1, proposers.Count(id => id == annaId));
        Assert.Equal(1, proposers.Count(id => id == carlId));
    }

    private sealed record MeDto(Guid? UserId, string? Subject, string? DisplayName);

    private sealed record ApprovalDto(
        Guid Id, Guid ConversationId, string ModuleId, string ToolName, string? ArgumentsJson,
        Guid? UserId, string? UserDisplay, DateTimeOffset CreatedAt, string Risk, string? Description);
}
