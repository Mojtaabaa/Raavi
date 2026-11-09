using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Identity.Domain;
using Raavi.Infrastructure.Persistence;
using Raavi.Infrastructure.Tenancy;

namespace Raavi.Identity.Persistence;

/// <summary>
/// Identity module persistence, schema <c>identity</c>. Global tables: Users, ExternalIdentities, AuthenticationSessions,
/// SystemRoleAssignments, SystemInvitations, SystemSecurityEvents. Tenant-owned (RLS): TenantMembers, TenantRoleAssignments,
/// TenantGroups, TenantGroupMembers, TenantInvitations, TenantAuthenticationProviders, TenantSecurityEvents.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenantContext)
    : RaaviDbContextBase(options, tenantContext)
{
    public const string Schema = "identity";

    public DbSet<User> Users => Set<User>();

    public DbSet<UserAvatar> Avatars => Set<UserAvatar>();

    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();

    public DbSet<AuthenticationSession> Sessions => Set<AuthenticationSession>();

    public DbSet<SystemRoleAssignment> SystemRoles => Set<SystemRoleAssignment>();

    public DbSet<SystemInvitation> SystemInvitations => Set<SystemInvitation>();

    public DbSet<SystemSecurityEvent> SystemSecurityEvents => Set<SystemSecurityEvent>();

    public DbSet<TenantMember> Members => Set<TenantMember>();

    public DbSet<TenantRoleAssignment> TenantRoles => Set<TenantRoleAssignment>();

    public DbSet<TenantGroup> Groups => Set<TenantGroup>();

    public DbSet<TenantGroupMember> GroupMembers => Set<TenantGroupMember>();

    public DbSet<TenantInvitation> Invitations => Set<TenantInvitation>();

    public DbSet<TenantAuthenticationProvider> ProviderPolicies => Set<TenantAuthenticationProvider>();

    public DbSet<TenantSecurityEvent> TenantSecurityEvents => Set<TenantSecurityEvent>();

    public DbSet<UserTenantMembership> MembershipIndex => Set<UserTenantMembership>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SyncMembershipIndex();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SyncMembershipIndex();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Keeps the global <see cref="UserTenantMembership"/> index in step with <see cref="TenantMember"/> changes in the same
    /// transaction. Memberships are only ever written inside their tenant's scope, so the scope tenant is the row's tenant.
    /// </summary>
    private void SyncMembershipIndex()
    {
        var changes = ChangeTracker.Entries<TenantMember>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (changes.Count == 0)
        {
            return;
        }

        var tenantId = CurrentTenantId;
        if (tenantId == Guid.Empty)
        {
            return; // the stamping interceptor rejects tenant-owned writes without a tenant
        }

        foreach (var entry in changes)
        {
            var member = entry.Entity;
            var index = MembershipIndex.Local.FirstOrDefault(i => i.UserId == member.UserId && i.TenantId == tenantId)
                ?? MembershipIndex.FirstOrDefault(i => i.UserId == member.UserId && i.TenantId == tenantId);
            if (entry.State == EntityState.Deleted)
            {
                if (index is not null)
                {
                    MembershipIndex.Remove(index);
                }
            }
            else if (index is null)
            {
                MembershipIndex.Add(UserTenantMembership.For(member.UserId, tenantId, member.Status));
            }
            else
            {
                index.SetStatus(member.Status);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Email).HasMaxLength(256);
            b.Property(x => x.NormalizedEmail).HasMaxLength(256);
            b.Property(x => x.DisplayName).HasMaxLength(User.MaxNameLength);
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.PasswordHash).HasMaxLength(512).IsUnicode(false);
            b.Property(x => x.SecurityStamp).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.ProtectedTotpSecret).HasMaxLength(1024).IsUnicode(false);
            b.Property(x => x.PreferredLocale).HasMaxLength(8).IsUnicode(false);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.Ignore(x => x.MfaEnabled);
            b.Property(x => x.MustChangePassword).HasDefaultValue(false);
            // Local sign-in name: unique among users that have an address.
            b.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
        });

        modelBuilder.Entity<UserAvatar>(b =>
        {
            b.ToTable("UserAvatars");
            b.HasKey(x => x.UserId);
            b.Ignore(x => x.Id);
            b.Property(x => x.ContentType).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.Data).HasColumnType("varbinary(max)");
            b.HasOne<User>().WithOne().HasForeignKey<UserAvatar>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalIdentity>(b =>
        {
            b.ToTable("ExternalIdentities");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.AuthenticationProviderId).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.ExternalSubjectId).HasMaxLength(256);
            b.Property(x => x.ExternalUsername).HasMaxLength(256);
            b.Property(x => x.ExternalEmail).HasMaxLength(256);
            b.HasIndex(x => new { x.AuthenticationProviderId, x.ExternalSubjectId }).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuthenticationSession>(b =>
        {
            b.ToTable("AuthenticationSessions");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            b.Property(x => x.ProviderId).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.SecurityStamp).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.RevokedReason).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.IpAddress).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.UserAgent).HasMaxLength(256);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.UserId, x.TenantId });
            b.HasIndex(x => new { x.TenantId, x.RevokedAt });
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SystemRoleAssignment>(b =>
        {
            b.ToTable("SystemRoleAssignments");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Role).HasConversion<int>();
            b.Property(x => x.GrantedBy).HasMaxLength(128);
            b.HasIndex(x => new { x.UserId, x.Role }).IsUnique();
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SystemInvitation>(b =>
        {
            b.ToTable("SystemInvitations");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Email).HasMaxLength(256);
            b.Property(x => x.NormalizedEmail).HasMaxLength(256);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            b.Property(x => x.PendingProtectedTotpSecret).HasMaxLength(1024).IsUnicode(false);
            b.Property(x => x.InvitedBy).HasMaxLength(128);
            b.HasIndex(x => x.TokenHash).IsUnique();
        });

        modelBuilder.Entity<SystemSecurityEvent>(b =>
        {
            b.ToTable("SystemSecurityEvents");
            b.Property(x => x.Id).ValueGeneratedNever();
            ConfigureEvent(b);
            b.HasIndex(x => x.OccurredAt);
        });

        modelBuilder.Entity<TenantMember>(b =>
        {
            b.ToTable("TenantMembers");
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique().IsClustered();
            b.HasIndex(x => x.UserId);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantRoleAssignment>(b =>
        {
            b.ToTable("TenantRoleAssignments");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Role).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.Role }).IsUnique();
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantGroup>(b =>
        {
            b.ToTable("TenantGroups");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(TenantGroup.MaxNameLength);
            b.Property(x => x.NormalizedName).HasMaxLength(TenantGroup.MaxNameLength);
            b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.Source).HasConversion<int>();
            b.Property(x => x.ExternalKey).HasMaxLength(256);
            b.HasAlternateKey(x => new { x.TenantId, x.Id });
            b.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
        });

        modelBuilder.Entity<TenantGroupMember>(b =>
        {
            b.ToTable("TenantGroupMembers");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.TenantId, x.GroupId, x.UserId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId });
            // Composite FK: a membership row can only point at a group of the same tenant (TENANT_ISOLATION.md §2 layer 6).
            b.HasOne<TenantGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).HasPrincipalKey(g => new { g.TenantId, g.Id }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantInvitation>(b =>
        {
            b.ToTable("TenantInvitations");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Email).HasMaxLength(256);
            b.Property(x => x.NormalizedEmail).HasMaxLength(256);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            b.Property(x => x.Role).HasConversion<int?>();
            b.Property(x => x.InvitedBy).HasMaxLength(128);
            b.HasIndex(x => new { x.TenantId, x.NormalizedEmail });
            b.HasIndex(x => x.TokenHash).IsUnique();
        });

        modelBuilder.Entity<TenantAuthenticationProvider>(b =>
        {
            b.ToTable("TenantAuthenticationProviders");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ProviderId).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Provisioning).HasConversion<int>();
            b.Property(x => x.AllowedEmailDomains).HasMaxLength(1000);
            b.Property(x => x.AllowedExternalGroups).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.ProviderId }).IsUnique();
        });

        modelBuilder.Entity<UserTenantMembership>(b =>
        {
            b.ToTable("UserTenantMemberships");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasConversion<int>();
            b.HasIndex(x => new { x.UserId, x.TenantId }).IsUnique();
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantSecurityEvent>(b =>
        {
            b.ToTable("TenantSecurityEvents");
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Id).ValueGeneratedNever();
            ConfigureEvent(b);
            b.HasIndex(x => new { x.TenantId, x.OccurredAt }).IsClustered();
        });

        base.OnModelCreating(modelBuilder);
    }

    private static void ConfigureEvent<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> b)
        where T : class
    {
        b.Property<string>("EventType").HasMaxLength(64).IsUnicode(false);
        b.Property<string>("ActorId").HasMaxLength(128);
        b.Property<string?>("IpAddress").HasMaxLength(64).IsUnicode(false);
        b.Property<string>("CorrelationId").HasMaxLength(64).IsUnicode(false);
        b.Property<string?>("Details").HasMaxLength(2000);
    }
}

/// <summary>Design-time factory for <c>dotnet ef</c> (migrations only; no tenant).</summary>
public sealed class IdentityDbContextDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=RaaviDesign;Integrated Security=true", sql =>
                sql.MigrationsHistoryTable("__EFMigrationsHistory", IdentityDbContext.Schema))
            .Options;
        return new IdentityDbContext(options, new TenantContextHolder());
    }
}

/// <summary>Applies identity migrations (after tenancy). Called by Raavi.Migrator and integration tests.</summary>
public static class IdentityMigrations
{
    public static async Task ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(services);
        await using (scope.ConfigureAwait(false))
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IdentityDbContext>(scope.ServiceProvider);
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
