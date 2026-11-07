using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Tenancy;

namespace Raavi.Infrastructure.Messaging;

public enum OutboxStatus
{
    Pending = 1,
    Processed = 2,
    Failed = 3,
}

/// <summary>
/// Transactional outbox row (ADR-017, DATABASE_DESIGN.md). Written by a module in the <b>same</b> SaveChanges as the
/// state change it describes; dispatched at-least-once by <see cref="OutboxDispatcher{TContext}"/> inside a scope bound to
/// the row's tenant. The table is deliberately global (no RLS): only the dispatcher reads it, payloads carry ids only (no
/// content, no secrets), and handlers always run in the owning tenant's scope (TENANT_ISOLATION.md §6).
/// </summary>
public sealed class OutboxMessage
{
    public const int MaxTypeLength = 128;
    public const int MaxAttempts = 10;

    private OutboxMessage() { }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = "{}";

    public string? CorrelationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public OutboxStatus Status { get; private set; } = OutboxStatus.Pending;

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage Create<TPayload>(Guid tenantId, string type, TPayload payload, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new InvalidOperationException("Outbox messages must belong to a tenant.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return new OutboxMessage
        {
            Id = SequentialGuid.New(),
            TenantId = tenantId,
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload, OutboxJson.Options),
            CorrelationId = Activity.Current?.TraceId.ToString(),
            OccurredAt = now,
            AvailableAt = now,
        };
    }

    internal void MarkProcessed(DateTimeOffset now)
    {
        Status = OutboxStatus.Processed;
        ProcessedAt = now;
        LockedUntil = null;
        LastError = null;
    }

    internal void MarkFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        LastError = error.Length > 1000 ? error[..1000] : error;
        LockedUntil = null;
        if (Attempts >= MaxAttempts)
        {
            Status = OutboxStatus.Failed;
            return;
        }

        // Exponential back-off: 5 s, 10 s, 20 s … capped at 30 min.
        AvailableAt = now + TimeSpan.FromSeconds(Math.Min(1800, 5 * Math.Pow(2, Attempts - 1)));
    }

    internal void Defer(DateTimeOffset until)
    {
        AvailableAt = until;
        LockedUntil = null;
    }
}

public static class OutboxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>An outbox event as seen by handlers.</summary>
public sealed record OutboxEvent(Guid Id, Guid TenantId, string Type, string PayloadJson, DateTimeOffset OccurredAt)
{
    public T Payload<T>() => JsonSerializer.Deserialize<T>(PayloadJson, OutboxJson.Options)
        ?? throw new InvalidOperationException($"Outbox event {Id} has an empty payload.");
}

/// <summary>
/// Consumer of outbox events. Resolved inside the event's tenant scope. Delivery is at-least-once: handlers must be
/// idempotent (typically keyed by <see cref="OutboxEvent.Id"/>).
/// </summary>
public interface IOutboxHandler
{
    bool CanHandle(string eventType);

    Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken);
}

/// <summary>A DbContext that owns an outbox table.</summary>
public interface IHasOutbox
{
    DbSet<OutboxMessage> Outbox { get; }
}

public static class OutboxModelExtensions
{
    public const string TableName = "Outbox";

    public static void ConfigureOutbox(this ModelBuilder modelBuilder, string schema)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable(TableName, schema);
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasMaxLength(OutboxMessage.MaxTypeLength).IsUnicode(false);
            b.Property(x => x.CorrelationId).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.LastError).HasMaxLength(1000);
            b.Property(x => x.Status).HasConversion<int>();
            b.HasIndex(x => new { x.Status, x.AvailableAt }).IsClustered(false);
            b.HasIndex(x => x.OccurredAt).IsClustered();
        });
    }
}

public sealed class OutboxOptions
{
    public const string Section = "Raavi:Messaging:Outbox";

    public int PollIntervalMilliseconds { get; set; } = 2000;

    public int BatchSize { get; set; } = 100;

    public int LockSeconds { get; set; } = 120;
}

/// <summary>
/// Claims due outbox rows (UPDLOCK/READPAST so several workers can run), then, per row, opens a scope bound to the row's
/// tenant and runs every matching handler. Rows of tenants that are not active are deferred, never processed outside
/// their tenant.
/// </summary>
public sealed partial class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopeFactory,
    ITenantScopeFactory tenantScopes,
    IClock clock,
    ILogger<OutboxDispatcher<TContext>> logger)
    where TContext : DbContext, IHasOutbox
{
    public async Task<int> DispatchOnceAsync(int batchSize = 100, int lockSeconds = 120, CancellationToken cancellationToken = default)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
            var claimed = await ClaimAsync(db, batchSize, lockSeconds, cancellationToken).ConfigureAwait(false);
            if (claimed.Count == 0)
            {
                return 0;
            }

            var messages = await db.Outbox.Where(m => claimed.Contains(m.Id)).OrderBy(m => m.OccurredAt).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var message in messages)
            {
                await ProcessAsync(directory, message, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return messages.Count;
        }
    }

    private async Task<List<Guid>> ClaimAsync(TContext db, int batchSize, int lockSeconds, CancellationToken ct)
    {
        var entity = db.Model.FindEntityType(typeof(OutboxMessage))!;
        var table = $"[{entity.GetSchema()}].[{entity.GetTableName()}]";
        var now = clock.UtcNow;
#pragma warning disable EF1002 // table name comes from the EF model, values are parameters
        return await db.Database.SqlQueryRaw<Guid>(
            $"""
            WITH due AS (
                SELECT TOP (@batch) Id, LockedUntil
                FROM {table} WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE Status = 1 AND AvailableAt <= @now AND (LockedUntil IS NULL OR LockedUntil < @now)
                ORDER BY OccurredAt)
            UPDATE due SET LockedUntil = @until
            OUTPUT inserted.Id AS Value;
            """,
            new SqlParameter("@batch", batchSize),
            new SqlParameter("@now", now),
            new SqlParameter("@until", now.AddSeconds(lockSeconds))).ToListAsync(ct).ConfigureAwait(false);
#pragma warning restore EF1002
    }

    private async Task ProcessAsync(ITenantDirectory directory, OutboxMessage message, CancellationToken ct)
    {
        var tenant = await directory.FindByIdAsync(message.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || !tenant.IsActive)
        {
            message.Defer(clock.UtcNow.AddMinutes(5));
            return;
        }

        var evt = new OutboxEvent(message.Id, message.TenantId, message.Type, message.PayloadJson, message.OccurredAt);
        try
        {
            var scope = tenantScopes.CreateScope(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.BackgroundJob, null));
            await using (scope.ConfigureAwait(false))
            {
                foreach (var handler in scope.ServiceProvider.GetServices<IOutboxHandler>().Where(h => h.CanHandle(message.Type)))
                {
                    await handler.HandleAsync(evt, ct).ConfigureAwait(false);
                }
            }

            message.MarkProcessed(clock.UtcNow);
        }
#pragma warning disable CA1031 // a failing handler must not stop the batch; the row is retried with back-off
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogHandlerFailed(logger, ex, message.Type, message.Id, message.TenantId);
            message.MarkFailed(ex.GetType().Name + ": " + ex.Message, clock.UtcNow);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox event {Type} {Id} of tenant {TenantId} failed")]
    private static partial void LogHandlerFailed(ILogger logger, Exception ex, string type, Guid id, Guid tenantId);
}

/// <summary>Polls one module's outbox (worker host only).</summary>
public sealed partial class OutboxDispatcherService<TContext>(OutboxDispatcher<TContext> dispatcher, IConfiguration configuration, ILogger<OutboxDispatcherService<TContext>> logger)
    : BackgroundService
    where TContext : DbContext, IHasOutbox
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.GetSection(OutboxOptions.Section).Get<OutboxOptions>() ?? new OutboxOptions();
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await dispatcher.DispatchOnceAsync(options.BatchSize, options.LockSeconds, stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // keep polling after transient database failures
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogDispatchFailed(logger, ex, typeof(TContext).Name);
            }

            if (processed == 0)
            {
                await Task.Delay(options.PollIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch for {Context} failed")]
    private static partial void LogDispatchFailed(ILogger logger, Exception ex, string context);
}

public static class OutboxServiceCollectionExtensions
{
    /// <summary>Registers the dispatcher of <typeparamref name="TContext"/>; pass <paramref name="runPoller"/> only in the worker host.</summary>
    public static IServiceCollection AddRaaviOutbox<TContext>(this IServiceCollection services, bool runPoller)
        where TContext : DbContext, IHasOutbox
    {
        services.AddSingleton<OutboxDispatcher<TContext>>();
        if (runPoller)
        {
            services.AddHostedService<OutboxDispatcherService<TContext>>();
        }

        return services;
    }
}
