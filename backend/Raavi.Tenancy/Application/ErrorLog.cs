using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Http;
using Raavi.Tenancy.Persistence;
using Raavi.Tenancy.Domain;

namespace Raavi.Tenancy.Application;

/// <summary>
/// Takes reported errors on a bounded queue and writes them in small batches on a background task, so a failing database can never slow a
/// request down (when the queue is full the oldest report is dropped). Also purges old entries.
/// </summary>
public sealed partial class ErrorLogWriter(IServiceScopeFactory scopes, ILogger<ErrorLogWriter> logger) : BackgroundService, IErrorSink
{
    private readonly Channel<ErrorReport> _queue = Channel.CreateBounded<ErrorReport>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not store {Count} error log entries.")]
    private static partial void LogStoreFailed(ILogger logger, Exception ex, int count);

    public void Report(ErrorReport report) => _queue.Writer.TryWrite(report);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ErrorLogEntry>();
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), stoppingToken).ConfigureAwait(false); // let a burst gather
                batch.Clear();
                while (batch.Count < 100 && _queue.Reader.TryRead(out var report))
                {
                    batch.Add(ErrorLogEntry.From(report));
                }

                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
                    db.ErrorLog.AddRange(batch);
                    await db.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogStoreFailed(logger, ex, batch.Count);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}

public sealed record ErrorLogItemDto(Guid Id, DateTimeOffset OccurredAt, string Source, string? TenantCode, Guid? UserId, string? Method, string? Path, int? StatusCode, string? ExceptionType, string Message, string? CorrelationId, string Fingerprint);

public sealed record ErrorLogDetailDto(ErrorLogItemDto Item, string? StackTrace, string? UserAgent, int SameCount);

public sealed record ErrorGroupDto(string Fingerprint, int Count, DateTimeOffset LastSeen, string Message, string? ExceptionType, string? Path, string Source);

public sealed record ErrorDayDto(DateOnly Date, int Server, int Client);

public sealed record ErrorSummaryDto(int Total, int Server, int Client, IReadOnlyList<ErrorDayDto> Days, IReadOnlyList<ErrorGroupDto> Groups);

public sealed record ErrorPage(int Total, IReadOnlyList<ErrorLogItemDto> Items);

/// <summary>Searching and reading the error log (system administrators).</summary>
public sealed class ErrorLogService(TenancyDbContext db, TimeProvider time)
{
    public const int MaxClientReportsPerRequest = 1;

    private static string Like(string q) => "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";

    private IQueryable<ErrorLogEntry> Filtered(string? q, string? source, DateTimeOffset? from, DateTimeOffset? to, string? fingerprint)
    {
        var query = db.ErrorLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(e => e.Source == source);
        }

        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            query = query.Where(e => e.Fingerprint == fingerprint);
        }

        if (from is { } f)
        {
            query = query.Where(e => e.OccurredAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(e => e.OccurredAt < t);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = Like(q.Trim());
            int? code = int.TryParse(q.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            query = query.Where(e =>
                EF.Functions.Like(e.Message, like, "\\") ||
                (e.ExceptionType != null && EF.Functions.Like(e.ExceptionType, like, "\\")) ||
                (e.Path != null && EF.Functions.Like(e.Path, like, "\\")) ||
                (e.StackTrace != null && EF.Functions.Like(e.StackTrace, like, "\\")) ||
                (e.TenantCode != null && EF.Functions.Like(e.TenantCode, like, "\\")) ||
                (e.CorrelationId != null && EF.Functions.Like(e.CorrelationId, like, "\\")) ||
                (e.Method != null && EF.Functions.Like(e.Method, like, "\\")) ||
                (e.UserAgent != null && EF.Functions.Like(e.UserAgent, like, "\\")) ||
                e.Fingerprint == q.Trim() ||
                (code != null && e.StatusCode == code));
        }

        return query;
    }

    private static ErrorLogItemDto ToItem(ErrorLogEntry e) => new(e.Id, e.OccurredAt, e.Source, e.TenantCode, e.UserId, e.Method, e.Path, e.StatusCode, e.ExceptionType, e.Message, e.CorrelationId, e.Fingerprint);

    public async Task<ErrorPage> SearchAsync(string? q, string? source, DateTimeOffset? from, DateTimeOffset? to, string? fingerprint, int skip, int take, CancellationToken ct)
    {
        var query = Filtered(q, source, from, to, fingerprint);
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query.OrderByDescending(e => e.OccurredAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct).ConfigureAwait(false);
        return new ErrorPage(total, items.Select(ToItem).ToList());
    }

    public async Task<Result<ErrorLogDetailDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var e = await db.ErrorLog.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (e is null)
        {
            return Result<ErrorLogDetailDto>.Failure(new Error("error.not_found", "Error not found.", ErrorKind.NotFound));
        }

        var same = await db.ErrorLog.CountAsync(x => x.Fingerprint == e.Fingerprint, ct).ConfigureAwait(false);
        return Result<ErrorLogDetailDto>.Success(new ErrorLogDetailDto(ToItem(e), e.StackTrace, e.UserAgent, same));
    }

    public async Task<ErrorSummaryDto> SummaryAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 90);
        var since = time.GetUtcNow().Date.AddDays(-(days - 1));
        var sinceOffset = new DateTimeOffset(since, TimeSpan.Zero);
        var rows = await db.ErrorLog.AsNoTracking().Where(e => e.OccurredAt >= sinceOffset).Select(e => new { e.OccurredAt, e.Source, e.Fingerprint, e.Message, e.ExceptionType, e.Path }).ToListAsync(ct).ConfigureAwait(false);
        var perDay = Enumerable.Range(0, days).Select(i => DateOnly.FromDateTime(since.AddDays(i))).Select(d => new ErrorDayDto(d, rows.Count(r => DateOnly.FromDateTime(r.OccurredAt.UtcDateTime) == d && r.Source == "server"), rows.Count(r => DateOnly.FromDateTime(r.OccurredAt.UtcDateTime) == d && r.Source == "client"))).ToList();
        var groups = rows.GroupBy(r => r.Fingerprint).Select(g =>
        {
            var last = g.OrderByDescending(x => x.OccurredAt).First();
            return new ErrorGroupDto(g.Key, g.Count(), last.OccurredAt, last.Message, last.ExceptionType, last.Path, last.Source);
        }).OrderByDescending(g => g.Count).ThenByDescending(g => g.LastSeen).Take(15).ToList();
        return new ErrorSummaryDto(rows.Count, rows.Count(r => r.Source == "server"), rows.Count(r => r.Source == "client"), perDay, groups);
    }

    public async Task<int> PurgeAsync(int olderThanDays, CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddDays(-Math.Max(0, olderThanDays));
        return await db.ErrorLog.Where(e => e.OccurredAt < cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}
