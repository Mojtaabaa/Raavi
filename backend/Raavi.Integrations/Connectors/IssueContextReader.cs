using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Ai;
using Raavi.Integrations.Persistence;

namespace Raavi.Integrations.Connectors;

/// <summary>Issue lookup for the Prompt Hub: uses the first enabled connector of the requested kind (the same lookup, host policy and rate limit as the issue chips on pages).</summary>
public sealed class IssueContextReader(IntegrationsDbContext db, IssueLookupService lookup) : IIssueContextReader
{
    public async Task<IssueContextResult> ReadAsync(string kind, string reference, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<IssueConnectorKind>(kind, ignoreCase: true, out var k))
        {
            return new IssueContextResult(IssueContextOutcome.NoConnector, null);
        }

        var connector = await db.IssueConnectors.AsNoTracking().Where(c => c.Kind == k && c.Enabled).OrderBy(c => c.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (connector is null)
        {
            return new IssueContextResult(IssueContextOutcome.NoConnector, null);
        }

        var result = await lookup.LookupAsync(connector.Id, reference, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new IssueContextResult(result.Error!.Code == "issue.not_found" ? IssueContextOutcome.NotFound : IssueContextOutcome.Unavailable, null);
        }

        var i = result.Value;
        return new IssueContextResult(IssueContextOutcome.Found, new IssueContext(k.ToString(), connector.Name, i.Reference, i.Title, i.Status, i.Url, i.Type));
    }
}
