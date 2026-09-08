using System.Net;
using System.Text;
using Plenipo.Testing.AgUi;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The kit's chat helper must say WHY a turn was refused: a product that hits a 4xx on
/// <c>/api/agui/{module}</c> needs the endpoint's body (the tool's own refusal, the validation
/// detail), not a bare status. No host involved — a stub handler plays the endpoint.
/// </summary>
public sealed class AgUiClientTests
{
    [Fact]
    public async Task ChatAsync_quotes_the_endpoints_body_when_the_turn_is_refused()
    {
        using var client = new HttpClient(new StubHandler(
            HttpStatusCode.UnprocessableEntity,
            "{\"detail\":\"Name at least one candidate to advance. (Parameter 'references')\"}"))
        {
            BaseAddress = new Uri("http://kit.test"),
        };

        var refusal = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ChatAsync("hiring", "Please advance candidates for me, using a tool."));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusal.StatusCode);
        Assert.Contains("/api/agui/hiring", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("422", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Name at least one candidate to advance", refusal.Message, StringComparison.Ordinal);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
