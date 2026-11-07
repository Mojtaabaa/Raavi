namespace Raavi.Domain.Common;

/// <summary>Base type for all persisted aggregates and entities. Identity is an internal, immutable, SQL-Server-ordered GUID.</summary>
public abstract class Entity
{
    public Guid Id { get; protected init; } = SequentialGuid.New();
}
