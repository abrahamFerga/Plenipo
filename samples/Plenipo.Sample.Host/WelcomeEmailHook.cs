using Plenipo.Application.Commerce;
using Plenipo.Application.Notifications;

namespace Plenipo.Sample.Host;

/// <summary>
/// The worked example of a product provisioning hook (docs: host extensibility): when a tenant is
/// provisioned — by an operator or a billing webhook — the new admin gets their welcome email.
/// Skips silently when email delivery isn't configured; the platform already guarantees a hook
/// failure never rolls back the tenant.
/// </summary>
public sealed class WelcomeEmailHook(ISmtpTransport smtp) : ITenantProvisionedHook
{
    public async Task OnTenantProvisionedAsync(TenantProvisionedContext context, CancellationToken cancellationToken = default)
    {
        if (!smtp.IsConfigured)
        {
            return;
        }

        await smtp.SendAsync(new EmailMessage(
            To: context.AdminEmail,
            Subject: $"Your workspace '{context.Name}' is ready",
            TextBody:
                $"Welcome! Your workspace '{context.Name}' ({context.Slug}) is live.\n\n" +
                $"Sign in with your account ({context.AdminEmail}) — you are the administrator.\n" +
                $"Modules enabled: {string.Join(", ", context.EnabledModules)}.\n" +
                (context.MaxSeats is { } seats ? $"Seats: {seats}.\n" : "") +
                "\nAdd your team under Admin → Users, and pick your AI setup under Admin → AI Settings."),
            cancellationToken);
    }
}
