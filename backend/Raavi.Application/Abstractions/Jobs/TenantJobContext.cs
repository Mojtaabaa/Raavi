namespace Raavi.Application.Abstractions.Jobs;

/// <summary>
/// Explicit tenant envelope carried by every background job that touches tenant-owned data.
/// The job runner re-validates the tenant (exists, active) before executing and fails the job otherwise.
/// A job without an envelope runs only in system scope and can never touch tenant-owned tables.
/// </summary>
public sealed record TenantJobContext(Guid TenantId, Guid? InitiatedByUserId, string CorrelationId);

/// <summary>A unit of background work executed inside an explicit tenant scope.</summary>
public interface ITenantJob<in TArgs>
{
    Task ExecuteAsync(TArgs args, CancellationToken cancellationToken);
}

/// <summary>Enqueues tenant jobs for the <b>currently resolved</b> tenant. Throws when no tenant is resolved.</summary>
public interface ITenantJobScheduler
{
    string Enqueue<TJob, TArgs>(TArgs args) where TJob : ITenantJob<TArgs>;

    /// <summary>Runs the job at <paramref name="runAt"/> (UTC) in the current tenant. Jobs must re-check their preconditions when they run.</summary>
    string Schedule<TJob, TArgs>(TArgs args, DateTimeOffset runAt) where TJob : ITenantJob<TArgs>;
}

/// <summary>Raised when a tenant job's tenant is missing or not active; the job is retried by the scheduler.</summary>
public sealed class TenantUnavailableException : Exception
{
    public TenantUnavailableException() : base("The tenant is not available.") { }

    public TenantUnavailableException(string message) : base(message) { }

    public TenantUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
