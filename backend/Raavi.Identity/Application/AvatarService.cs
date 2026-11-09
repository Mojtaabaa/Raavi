using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;

namespace Raavi.Identity.Application;

public sealed class AvatarOptions
{
    public const string Section = "Raavi:Avatars";

    /// <summary>Largest accepted file. The SPA resizes to 256 px before uploading, so a few tens of kilobytes is normal.</summary>
    public int MaxBytes { get; set; } = 512 * 1024;

    /// <summary>Largest accepted width/height in pixels (a tiny file can describe a huge picture that a browser would have to decode).</summary>
    public int MaxPixels { get; set; } = 1024;
}

/// <summary>Recognises PNG, JPEG and WebP by their content (never by the declared type or file name) and reads their pixel size. SVG and everything else is refused.</summary>
public static class AvatarImage
{
    public static (string ContentType, int Width, int Height)? Inspect(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A
            && d[12] == (byte)'I' && d[13] == (byte)'H' && d[14] == (byte)'D' && d[15] == (byte)'R')
        {
            return ("image/png", BigEndian32(d[16..]), BigEndian32(d[20..]));
        }

        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            return Jpeg(d);
        }

        if (d.Length >= 30 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
        {
            return WebP(d);
        }

        return null;
    }

    private static int BigEndian32(ReadOnlySpan<byte> d) => (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3];

    private static (string, int, int)? Jpeg(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF)
            {
                return null;
            }

            var marker = d[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }

            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isStartOfFrame)
            {
                var height = (d[i + 5] << 8) | d[i + 6];
                var width = (d[i + 7] << 8) | d[i + 8];
                return ("image/jpeg", width, height);
            }

            var length = (d[i + 2] << 8) | d[i + 3];
            if (length < 2)
            {
                return null;
            }

            i += 2 + length;
        }

        return null;
    }

    private static (string, int, int)? WebP(ReadOnlySpan<byte> d)
    {
        if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == ' ' && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)
        {
            return ("image/webp", (d[26] | (d[27] << 8)) & 0x3FFF, (d[28] | (d[29] << 8)) & 0x3FFF);
        }

        if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'L' && d[20] == 0x2F)
        {
            var width = 1 + (((d[22] & 0x3F) << 8) | d[21]);
            var height = 1 + (((d[24] & 0x0F) << 10) | (d[23] << 2) | ((d[22] & 0xC0) >> 6));
            return ("image/webp", width, height);
        }

        if (d[12] == 'V' && d[13] == 'P' && d[14] == '8' && d[15] == 'X')
        {
            var width = 1 + (d[24] | (d[25] << 8) | (d[26] << 16));
            var height = 1 + (d[27] | (d[28] << 8) | (d[29] << 16));
            return ("image/webp", width, height);
        }

        return null;
    }
}

public sealed record AvatarFile(byte[] Data, string ContentType, int Version);

/// <summary>Profile pictures (ADR-046).</summary>
public sealed class AvatarService(IdentityDbContext db, ICurrentUser current, IClock clock, IOptions<AvatarOptions> options)
{
    public const int MaxBatch = 100;

    public static readonly Error TooLarge = new("avatar.too_large", "The picture is too large.", ErrorKind.Validation);
    public static readonly Error NotAnImage = new("avatar.type_invalid", "Only PNG, JPEG or WebP pictures are accepted.", ErrorKind.Validation);
    public static readonly Error BadSize = new("avatar.size_invalid", "The picture's width and height must be between 16 and the allowed maximum.", ErrorKind.Validation);
    public static readonly Error NotFound = new("avatar.not_found", "No picture.", ErrorKind.NotFound);

    /// <summary>Stores the signed-in user's picture; returns its new version.</summary>
    public Task<Result<int>> SetAsync(Stream content, long declaredLength, CancellationToken ct) => SetForAsync(current.UserId, content, declaredLength, ct);

    /// <summary>Stores the picture of <paramref name="userId"/> (own picture, or a workspace administrator's change — the caller has checked that right).</summary>
    public async Task<Result<int>> SetForAsync(Guid userId, Stream content, long declaredLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var max = options.Value.MaxBytes;
        if (declaredLength > max)
        {
            return Result<int>.Failure(TooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return Result<int>.Failure(TooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        var info = AvatarImage.Inspect(bytes);
        if (info is null)
        {
            return Result<int>.Failure(NotAnImage);
        }

        var (type, width, height) = info.Value;
        var limit = options.Value.MaxPixels;
        if (width < 16 || height < 16 || width > limit || height > limit)
        {
            return Result<int>.Failure(BadSize);
        }

        var existing = await db.Avatars.FirstOrDefaultAsync(a => a.UserId == userId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            existing = UserAvatar.Create(userId, type, bytes, clock.UtcNow);
            db.Avatars.Add(existing);
        }
        else
        {
            existing.Replace(type, bytes, clock.UtcNow);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<int>.Success(existing.Version);
    }

    public Task<Result> RemoveAsync(CancellationToken ct) => RemoveForAsync(current.UserId, ct);

    public async Task<Result> RemoveForAsync(Guid userId, CancellationToken ct)
    {
        var existing = await db.Avatars.FirstOrDefaultAsync(a => a.UserId == userId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            db.Avatars.Remove(existing);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>The picture of <paramref name="userId"/> for a member of the workspace of this request. Not found for everything else (no hint whether the user exists).</summary>
    public async Task<Result<AvatarFile>> GetAsync(Guid userId, CancellationToken ct)
    {
        if (!await MayView(userId, ct).ConfigureAwait(false))
        {
            return Result<AvatarFile>.Failure(NotFound);
        }

        var a = await db.Avatars.AsNoTracking().Where(x => x.UserId == userId).Select(x => new AvatarFile(x.Data, x.ContentType, x.Version)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return a is null ? Result<AvatarFile>.Failure(NotFound) : Result<AvatarFile>.Success(a);
    }

    /// <summary>Versions of the pictures that exist among <paramref name="userIds"/> (members of this workspace, or the caller). Users without a picture are simply absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> VersionsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().Take(MaxBatch).ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var me = current.UserId;
        var members = await db.Members.AsNoTracking().Where(m => ids.Contains(m.UserId) && m.Status == MembershipStatus.Active).Select(m => m.UserId).ToListAsync(ct).ConfigureAwait(false);
        var visible = new HashSet<Guid>(members);
        if (ids.Contains(me))
        {
            visible.Add(me);
        }

        var candidates = visible.ToList();
        return await db.Avatars.AsNoTracking().Where(a => candidates.Contains(a.UserId)).ToDictionaryAsync(a => a.UserId, a => a.Version, ct).ConfigureAwait(false);
    }

    private async Task<bool> MayView(Guid userId, CancellationToken ct) =>
        userId == current.UserId
        || await db.Members.AsNoTracking().AnyAsync(m => m.UserId == userId && m.Status == MembershipStatus.Active, ct).ConfigureAwait(false);
}
