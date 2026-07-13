using System.Net;
using Cortex.Application.Notifications;
using Cortex.Application.Security;
using Cortex.Infrastructure.Notifications;
using Cortex.Infrastructure.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Tests;

public sealed class WebhookChannelTests
{
    [Fact]
    public void Signature_MatchesTheDocumentedScheme()
    {
        var signature = WebhookSignature.Compute("""{"title":"x"}""", "shhh");

        Assert.StartsWith("sha256=", signature, StringComparison.Ordinal);
        Assert.Equal(7 + 64, signature.Length);
        Assert.Equal(signature, WebhookSignature.Compute("""{"title":"x"}""", "shhh")); // deterministic
        Assert.NotEqual(signature, WebhookSignature.Compute("""{"title":"x"}""", "other-secret"));
    }

    [Fact]
    public async Task Send_PostsSignedPayload_ToTheConfiguredUrl()
    {
        var vault = new DataProtectionSecretVault(new EphemeralDataProtectionProvider());
        var secretRef = await vault.StoreAsync(WebhookNotificationChannel.SecretScope, "shhh");
        var handler = new CapturingHandler();
        var channel = new WebhookNotificationChannel(
            new FixedConfig(new NotificationWebhookConfig("https://hooks.example/cortex", secretRef)),
            vault,
            new SingleClientFactory(handler),
            PermissivePolicy());

        await channel.SendAsync(new Notification(Guid.NewGuid(), Guid.NewGuid(), "jobs", "Job finished", "Done."));

        Assert.NotNull(handler.Request);
        Assert.Equal("https://hooks.example/cortex", handler.Request!.RequestUri!.ToString());
        var signature = Assert.Single(handler.Request.Headers.GetValues(WebhookSignature.HeaderName));
        Assert.Equal(WebhookSignature.Compute(handler.Body!, "shhh"), signature); // signs the exact body sent
        Assert.Contains("\"title\":\"Job finished\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_WithoutConfig_DoesNothing()
    {
        var handler = new CapturingHandler();
        var channel = new WebhookNotificationChannel(
            new FixedConfig(null),
            new DataProtectionSecretVault(new EphemeralDataProtectionProvider()),
            new SingleClientFactory(handler),
            PermissivePolicy());

        await channel.SendAsync(new Notification(Guid.NewGuid(), Guid.NewGuid(), "jobs", "t", "b"));

        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Send_BlocksPrivateDestinations()
    {
        var handler = new CapturingHandler();
        var channel = new WebhookNotificationChannel(
            new FixedConfig(new NotificationWebhookConfig("http://169.254.169.254/latest/meta-data", null)),
            new DataProtectionSecretVault(new EphemeralDataProtectionProvider()),
            new SingleClientFactory(handler),
            new OutboundUrlPolicy(Options.Create(new OutboundUrlOptions { AllowHttp = true })));

        await Assert.ThrowsAsync<ArgumentException>(() => channel.SendAsync(
            new Notification(Guid.NewGuid(), Guid.NewGuid(), "jobs", "t", "b")));
        Assert.Null(handler.Request);
    }

    private static OutboundUrlPolicy PermissivePolicy() =>
        new(Options.Create(new OutboundUrlOptions { AllowHttp = true, AllowPrivateNetworks = true }));

    private sealed class FixedConfig(NotificationWebhookConfig? config) : INotificationWebhookConfigReader
    {
        public Task<NotificationWebhookConfig?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(config);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
