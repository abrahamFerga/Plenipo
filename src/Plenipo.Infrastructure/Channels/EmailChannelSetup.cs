using Plenipo.Application.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Channels;

/// <summary>
/// Wires the email-intake channel: options (validated at startup, fail-fast), the MailKit inbox,
/// the per-poll service, and the polling loop. When <c>Channels:Email:Enabled</c> is false (the
/// default) the poller exits immediately and nothing else is active, so hosts that don't use email
/// intake carry no behavior change. No endpoints — this is the first poll-pull channel.
/// </summary>
public static class EmailChannelSetup
{
    public static IServiceCollection AddPlenipoEmailChannel(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailChannelOptions>(configuration.GetSection(EmailChannelOptions.SectionName));

        // Fail fast on an enabled-but-incomplete channel, at startup rather than on the first poll.
        var options = configuration.GetSection(EmailChannelOptions.SectionName).Get<EmailChannelOptions>() ?? new EmailChannelOptions();
        options.ThrowIfInvalid();

        services.AddSingleton<IImapInbox, MailKitImapInbox>();
        services.AddScoped<EmailChannelService>();
        services.AddHostedService<EmailChannelPoller>();

        return services;
    }
}

/// <summary>
/// Polls the intake mailbox on the configured cadence. Waits a full interval before the first poll
/// (startup work first; also lets tests drive <see cref="EmailChannelService.PollOnceAsync"/>
/// deterministically), and a failed poll logs and waits for the next tick — mail is never lost,
/// the watermark only advances after a completed batch.
/// </summary>
public sealed class EmailChannelPoller(
    IServiceScopeFactory scopes,
    IOptions<EmailChannelOptions> options,
    ILogger<EmailChannelPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.PollSeconds));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var channel = scope.ServiceProvider.GetRequiredService<EmailChannelService>();
                    await channel.PollOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Email intake poll failed; retrying on the next tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }
}
