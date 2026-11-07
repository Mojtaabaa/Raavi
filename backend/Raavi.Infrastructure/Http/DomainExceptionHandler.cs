using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Common;
using Raavi.Domain.Common;

namespace Raavi.Infrastructure.Http;

/// <summary>
/// Maps expected exceptions to problem details with a stable <c>code</c>: domain rule violations → 400, lost optimistic
/// concurrency races (a row changed between read and write) → 412. Everything else stays a generic 500.
/// </summary>
public sealed class DomainExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        Error? error = exception switch
        {
            DomainException d => new Error(d.Code, d.Message),
            DbUpdateConcurrencyException => ETags.PreconditionFailed,
            _ => null,
        };
        if (error is null)
        {
            return false;
        }

        await error.ToProblem().ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }
}
