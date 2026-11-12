using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Calendar;

namespace Raavi.Wiki.Application;

public sealed class TemplarCalendarOptions
{
    public const string Section = "Raavi:Calendar:Templar";

    /// <summary>When false or BaseUrl empty, the provider reports unconfigured and returns no occurrences.</summary>
    public bool Enabled { get; set; }

    public string? BaseUrl { get; set; }

    /// <summary>Secret via RAAVI_Raavi__Calendar__Templar__ApiKey — never logged.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Templar shell: configured only when Enabled + BaseUrl; live HTTP stays Blocked (empty feed) until the contract is implemented.</summary>
public sealed class DisabledTemplarCalendarProvider(IOptions<TemplarCalendarOptions> options) : ITemplarCalendarProvider
{
    public bool IsConfigured
    {
        get
        {
            var o = options.Value;
            return o.Enabled && !string.IsNullOrWhiteSpace(o.BaseUrl);
        }
    }

    public Task<IReadOnlyList<CalendarOverlayOccurrence>> GetOccurrencesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CalendarOverlayOccurrence>>([]);
}
