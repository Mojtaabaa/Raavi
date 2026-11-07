namespace Raavi.Domain.Common;

/// <summary>
/// GUIDs that sort sequentially under SQL Server's uniqueidentifier ordering (which compares bytes 10–15 first),
/// avoiding clustered index fragmentation (DATABASE_DESIGN.md §1). Same layout as EF Core's SequentialGuidValueGenerator.
/// </summary>
public static class SequentialGuid
{
    private static long _counter = DateTime.UtcNow.Ticks;

    public static Guid New()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var counter = BitConverter.GetBytes(Interlocked.Increment(ref _counter));
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        bytes[8] = counter[1];
        bytes[9] = counter[0];
        bytes[10] = counter[7];
        bytes[11] = counter[6];
        bytes[12] = counter[5];
        bytes[13] = counter[4];
        bytes[14] = counter[3];
        bytes[15] = counter[2];
        return new Guid(bytes);
    }
}
