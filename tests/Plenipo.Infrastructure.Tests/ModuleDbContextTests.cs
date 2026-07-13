using Plenipo.Core.Entities;
using Plenipo.Modules.Sdk;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// The module persistence base must make unstamped audit fields unrepresentable: a module
/// context deriving straight from DbContext silently persists default(DateTimeOffset)
/// timestamps, and everything ordered/filtered by recency breaks without a single failing write
/// (found the hard way by a product's "activity log is always empty" bug).
/// </summary>
public class ModuleDbContextTests
{
    private sealed class Widget : EntityBase
    {
        public string Name { get; set; } = "";
    }

    private sealed class WidgetContext(DbContextOptions<WidgetContext> options) : ModuleDbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
    }

    private static WidgetContext NewContext() => new(
        new DbContextOptionsBuilder<WidgetContext>()
            .UseInMemoryDatabase($"module-db-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Adds_stamp_CreatedAt_on_every_save_path()
    {
        await using var db = NewContext();
        var viaAsync = new Widget { Name = "async" };
        db.Widgets.Add(viaAsync);
        await db.SaveChangesAsync();

        var viaSync = new Widget { Name = "sync" };
        db.Widgets.Add(viaSync);
        db.SaveChanges();

        Assert.NotEqual(default, viaAsync.CreatedAt);
        Assert.NotEqual(default, viaSync.CreatedAt);
        Assert.Null(viaAsync.UpdatedAt);
    }

    [Fact]
    public async Task Modifications_stamp_UpdatedAt_and_preserve_CreatedAt()
    {
        await using var db = NewContext();
        var widget = new Widget { Name = "before" };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        var created = widget.CreatedAt;

        widget.Name = "after";
        await db.SaveChangesAsync();

        Assert.Equal(created, widget.CreatedAt);
        Assert.NotNull(widget.UpdatedAt);
    }

    [Fact]
    public async Task A_caller_supplied_CreatedAt_wins()
    {
        // Imports carry source timestamps; the stamp must not overwrite them.
        await using var db = NewContext();
        var imported = new Widget { Name = "imported", CreatedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero) };
        db.Widgets.Add(imported);
        await db.SaveChangesAsync();

        Assert.Equal(2020, imported.CreatedAt.Year);
    }
}
