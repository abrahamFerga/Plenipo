using Plenipo.Core.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Plenipo.Infrastructure.Persistence.Configurations;

/// <summary>Local auth mode storage (ADR 0003): credentials and the deployment's issuer keys.</summary>
internal sealed class LocalCredentialConfiguration : IEntityTypeConfiguration<LocalCredential>
{
    public void Configure(EntityTypeBuilder<LocalCredential> b)
    {
        b.ToTable("local_credentials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).HasMaxLength(320).IsRequired();
        b.Property(x => x.PasswordHash).IsRequired();
        b.Property(x => x.SecurityStamp).HasMaxLength(64).IsRequired();

        // Unique across the DEPLOYMENT, deliberately not per tenant: the anonymous login form has no
        // tenant field, so an email must resolve to exactly one credential on this host (ADR 0003).
        b.HasIndex(x => x.Email).IsUnique();

        b.HasIndex(x => x.TenantId);
        b.HasOne<User>().WithOne().HasForeignKey<LocalCredential>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserId).IsUnique();
    }
}

internal sealed class LocalAuthKeyConfiguration : IEntityTypeConfiguration<LocalAuthKey>
{
    public void Configure(EntityTypeBuilder<LocalAuthKey> b)
    {
        b.ToTable("local_auth_keys");
        b.HasKey(x => x.Id);
        b.Property(x => x.Use).HasMaxLength(8).IsRequired();
        b.Property(x => x.ProtectedKey).IsRequired();

        // One key per purpose; the unique index is what arbitrates a multi-instance first-boot race
        // (LocalAuthKeyRing catches the loser's DbUpdateException and re-reads the winner's key).
        b.HasIndex(x => x.Use).IsUnique();
    }
}
