namespace Raavi.Application.Abstractions.Time;

/// <summary>Testable source of UTC time. All persisted timestamps are UTC.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
