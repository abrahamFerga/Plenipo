using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// Push device registration: the endpoint the mobile shell calls on every launch. Self-scoped like
/// the notification inbox — a caller registers, lists, and forgets only their own installations —
/// and idempotent per installation, because push tokens rotate and a shell must be able to call
/// this unconditionally without piling up dead rows.
/// </summary>
public sealed class DeviceRegistrationTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public DeviceRegistrationTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    private static object Registration(
        string installationId, string pushToken, string platform = "Ios", string? deviceName = null) =>
        new { installationId, pushToken, platform, deviceName };

    private async Task<List<UserDevice>> DevicesOfAsync(string subject)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Subject == subject);
        return await db.UserDevices.IgnoreQueryFilters().Where(d => d.UserId == user.Id).ToListAsync();
    }

    [Fact]
    public async Task Registering_stores_the_device_but_never_echoes_the_token_back()
    {
        using var client = ClientAs("device-register");
        var response = await client.PutAsJsonAsync(
            "/api/notifications/devices", Registration("install-1", "ExponentPushToken[abc123]", "Android", "Pixel 9"));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("install-1", body.GetProperty("installationId").GetString());
        Assert.Equal("Android", body.GetProperty("platform").GetString());
        Assert.Equal("Pixel 9", body.GetProperty("deviceName").GetString());
        // A push token is a device identifier: it goes one way. Nothing in the response carries it.
        Assert.DoesNotContain("ExponentPushToken", body.GetRawText(), StringComparison.Ordinal);

        var stored = Assert.Single(await DevicesOfAsync("device-register"));
        Assert.Equal("ExponentPushToken[abc123]", stored.PushToken);
        Assert.Equal(DevicePlatform.Android, stored.Platform);
    }

    [Fact]
    public async Task Re_registering_the_same_installation_rotates_the_token_instead_of_adding_a_row()
    {
        using var client = ClientAs("device-rotate");

        (await client.PutAsJsonAsync("/api/notifications/devices", Registration("install-rot", "token-old")))
            .EnsureSuccessStatusCode();
        // What happens on the next launch after the OS reissues the token.
        (await client.PutAsJsonAsync("/api/notifications/devices", Registration("install-rot", "token-new")))
            .EnsureSuccessStatusCode();

        var device = Assert.Single(await DevicesOfAsync("device-rotate"));
        Assert.Equal("token-new", device.PushToken);
    }

    [Fact]
    public async Task A_second_installation_is_a_second_device()
    {
        using var client = ClientAs("device-two");

        (await client.PutAsJsonAsync("/api/notifications/devices", Registration("install-phone", "t-phone")))
            .EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/notifications/devices", Registration("install-tablet", "t-tablet")))
            .EnsureSuccessStatusCode();

        var devices = await DevicesOfAsync("device-two");
        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public async Task Listing_returns_only_the_callers_own_devices_and_no_tokens()
    {
        using var mine = ClientAs("device-mine");
        using var theirs = ClientAs("device-theirs");
        (await mine.PutAsJsonAsync("/api/notifications/devices", Registration("install-mine", "t-mine")))
            .EnsureSuccessStatusCode();
        (await theirs.PutAsJsonAsync("/api/notifications/devices", Registration("install-theirs", "t-theirs")))
            .EnsureSuccessStatusCode();

        var listed = await mine.GetFromJsonAsync<JsonElement>("/api/notifications/devices");

        var ids = listed.EnumerateArray().Select(d => d.GetProperty("installationId").GetString()).ToList();
        Assert.Equal(["install-mine"], ids);
        Assert.DoesNotContain("t-mine", listed.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forgetting_a_device_removes_it_outright()
    {
        using var client = ClientAs("device-forget");
        (await client.PutAsJsonAsync("/api/notifications/devices", Registration("install-gone", "t-gone")))
            .EnsureSuccessStatusCode();

        var deleted = await client.DeleteAsync("/api/notifications/devices/install-gone");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // Deleted, not flagged: once a device shouldn't be reached, keeping its token serves nobody.
        Assert.Empty(await DevicesOfAsync("device-forget"));
    }

    [Fact]
    public async Task Cannot_forget_someone_elses_device()
    {
        using var owner = ClientAs("device-owner");
        using var stranger = ClientAs("device-stranger");
        (await owner.PutAsJsonAsync("/api/notifications/devices", Registration("install-owned", "t-owned")))
            .EnsureSuccessStatusCode();

        var attempt = await stranger.DeleteAsync("/api/notifications/devices/install-owned");

        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        Assert.Single(await DevicesOfAsync("device-owner"));
    }

    [Fact]
    public async Task Rejects_an_unknown_platform_rather_than_storing_a_device_nothing_can_deliver_to()
    {
        using var client = ClientAs("device-bad-platform");
        var response = await client.PutAsJsonAsync(
            "/api/notifications/devices", Registration("install-bad", "t-bad", platform: "Blackberry"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await DevicesOfAsync("device-bad-platform"));
    }

    [Theory]
    [InlineData("", "a-token")]
    [InlineData("install-x", "")]
    public async Task Requires_both_an_installation_id_and_a_token(string installationId, string pushToken)
    {
        using var client = ClientAs("device-incomplete");
        var response = await client.PutAsJsonAsync(
            "/api/notifications/devices", Registration(installationId, pushToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Evicts_the_quietest_device_rather_than_letting_one_user_grow_the_table_without_bound()
    {
        using var client = ClientAs("device-cap");
        // The default cap is 10; register one past it. The first installation is the least
        // recently seen, so it is the one that makes way.
        for (var i = 0; i <= 10; i++)
        {
            (await client.PutAsJsonAsync("/api/notifications/devices", Registration($"install-{i:00}", $"t-{i:00}")))
                .EnsureSuccessStatusCode();
        }

        var devices = await DevicesOfAsync("device-cap");
        Assert.Equal(10, devices.Count);
        Assert.DoesNotContain(devices, d => d.InstallationId == "install-00");
        Assert.Contains(devices, d => d.InstallationId == "install-10");
    }
}
