using DnsClient;

namespace Raavi.Tenancy.Application;

/// <summary>DNS TXT lookups for domain verification. Abstracted so tests (and air-gapped installs with a custom resolver) can replace it.</summary>
public interface IDnsTxtResolver
{
    Task<IReadOnlyList<string>> QueryTxtAsync(string name, CancellationToken cancellationToken);
}

public sealed class DnsClientTxtResolver : IDnsTxtResolver
{
    private readonly LookupClient _client = new(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 });

    public async Task<IReadOnlyList<string>> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        var response = await _client.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Answers.TxtRecords().SelectMany(r => r.Text).ToList();
    }
}
