using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Persistence;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tenancy.Domain;

namespace Raavi.Tenancy.Persistence;

/// <summary>Tenancy module persistence: schema <c>system</c> (tenants, domains, settings, branding, system audit) and <c>audit</c> (tenant audit).</summary>
public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ITenantContext tenantContext)
    : RaaviDbContextBase(options, tenantContext)
{
    public const string Schema = "system";
    public const string AuditSchema = "audit";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();

    public DbSet<TenantBranding> TenantBranding => Set<TenantBranding>();

    public DbSet<TenantLogo> TenantLogos => Set<TenantLogo>();

    public DbSet<ErrorLogEntry> ErrorLog => Set<ErrorLogEntry>();

    public DbSet<SystemAuditLog> SystemAuditLogs => Set<SystemAuditLog>();

    public DbSet<TenantAuditLog> TenantAuditLogs => Set<TenantAuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("Tenants");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(16).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            b.Property(x => x.Slug).HasMaxLength(63).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            b.Property(x => x.DisplayNameFa).HasMaxLength(Tenant.MaxDisplayNameLength);
            b.Property(x => x.DisplayNameEn).HasMaxLength(Tenant.MaxDisplayNameLength);
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.StatusReason).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => x.Code).IsUnique();
            b.HasIndex(x => x.Slug).IsUnique();
            b.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<TenantDomain>(b =>
        {
            b.ToTable("TenantDomains");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Host).HasMaxLength(253).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.VerificationToken).HasMaxLength(64).IsUnicode(false);
            b.Ignore(x => x.IsRemoved);
            b.Ignore(x => x.Resolves);
            // One live mapping per host across all tenants; removed rows are kept for quarantine and audit.
            b.HasIndex(x => x.Host).IsUnique().HasFilter("[RemovedAt] IS NULL");
            b.HasIndex(x => new { x.TenantId, x.RemovedAt });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantSettings>(b =>
        {
            b.ToTable("TenantSettings");
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.TenantId).IsUnique().IsClustered();
            b.Property(x => x.DefaultLocale).HasMaxLength(8).IsUnicode(false);
            b.Property(x => x.TimeZone).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Calendar).HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ErrorLogEntry>(b =>
        {
            b.ToTable("ErrorLog");
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Source).HasMaxLength(8).IsUnicode(false);
            b.Property(x => x.TenantCode).HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.Method).HasMaxLength(10).IsUnicode(false);
            b.Property(x => x.Path).HasMaxLength(300);
            b.Property(x => x.ExceptionType).HasMaxLength(200);
            b.Property(x => x.Message).HasMaxLength(1100);
            b.Property(x => x.StackTrace).HasMaxLength(6100);
            b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.UserAgent).HasMaxLength(250);
            b.Property(x => x.Fingerprint).HasMaxLength(16).IsUnicode(false);
            b.HasIndex(x => x.OccurredAt).IsClustered();
            b.HasIndex(x => new { x.Fingerprint, x.OccurredAt });
        });

        modelBuilder.Entity<TenantLogo>(b =>
        {
            b.ToTable("TenantLogos");
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.TenantId).IsUnique().IsClustered();
            b.Property(x => x.ContentType).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.Data).HasColumnType("varbinary(max)");
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantBranding>(b =>
        {
            b.ToTable("TenantBranding");
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.TenantId).IsUnique().IsClustered();
            b.Property(x => x.PrimaryColor).HasMaxLength(7).IsUnicode(false);
            b.Property(x => x.SecondaryColor).HasMaxLength(7).IsUnicode(false);
            b.Property(x => x.WelcomeMessageFa).HasMaxLength(Domain.TenantBranding.MaxWelcomeLength);
            b.Property(x => x.WelcomeMessageEn).HasMaxLength(Domain.TenantBranding.MaxWelcomeLength);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SystemAuditLog>(b =>
        {
            b.ToTable("SystemAuditLogs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ActorType).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.ActorId).HasMaxLength(128);
            b.Property(x => x.Action).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.TargetType).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.TargetId).HasMaxLength(128);
            b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Details).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.OccurredAt });
            b.HasIndex(x => x.OccurredAt);
        });

        modelBuilder.Entity<TenantAuditLog>(b =>
        {
            b.ToTable("TenantAuditLogs", AuditSchema);
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.TenantId, x.OccurredAt }).IsClustered();
            b.Property(x => x.ActorType).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.ActorId).HasMaxLength(128);
            b.Property(x => x.Action).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.TargetType).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.TargetId).HasMaxLength(128);
            b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Details).HasMaxLength(2000);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>Design-time factory for <c>dotnet ef</c> (migrations only; no tenant).</summary>
public sealed class TenancyDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=RaaviDesign;Integrated Security=true", sql =>
                sql.MigrationsHistoryTable("__EFMigrationsHistory", TenancyDbContext.Schema))
            .Options;
        return new TenancyDbContext(options, new TenantContextHolder());
    }
}
