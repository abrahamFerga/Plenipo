using Cortex.Application.Documents;
using Cortex.Infrastructure.Documents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cortex.Api.Tests;

/// <summary>
/// The config-driven OCR engine: no configuration means no engine (and therefore no
/// <c>ocr_document</c> tool — the model never sees a capability the deployment lacks);
/// Ocr:Provider=AzureDocumentIntelligence with endpoint + key registers the Azure engine.
/// </summary>
public sealed class OcrConfigTests
{
    [Fact]
    public async Task No_configuration_means_no_engine()
    {
        using var factory = new CortexApiFactory();
        using var warmup = factory.CreateClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();

        Assert.Null(factory.Services.GetService<IOcrEngine>());
    }

    [Fact]
    public async Task Configured_provider_registers_the_azure_engine()
    {
        using var factory = new AzureOcrFactory();
        using var warmup = factory.CreateClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();

        var engine = factory.Services.GetService<IOcrEngine>();
        Assert.IsType<AzureDocumentIntelligenceOcrEngine>(engine);
        Assert.Equal("azure-document-intelligence", engine!.Name);
    }

    [Fact]
    public async Task A_provider_without_its_key_stays_off_instead_of_failing_at_first_use()
    {
        using var factory = new KeylessOcrFactory();
        using var warmup = factory.CreateClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();

        Assert.Null(factory.Services.GetService<IOcrEngine>());
    }

    private sealed class AzureOcrFactory : CortexApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ocr:Provider", "AzureDocumentIntelligence");
            builder.UseSetting("Ocr:Endpoint", "https://ocr-tests.cognitiveservices.azure.com/");
            builder.UseSetting("Ocr:ApiKey", "test-key-never-called");
            base.ConfigureWebHost(builder);
        }
    }

    private sealed class KeylessOcrFactory : CortexApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ocr:Provider", "AzureDocumentIntelligence");
            builder.UseSetting("Ocr:Endpoint", "https://ocr-tests.cognitiveservices.azure.com/");
            base.ConfigureWebHost(builder);
        }
    }
}
