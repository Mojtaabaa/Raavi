using Raavi.Domain.Common;

namespace Raavi.Identity.Domain;

/// <summary>A user's profile picture: global to the user (like the account), served only to members of the workspace the request is made in.</summary>
public sealed class UserAvatar : Entity
{
    private UserAvatar() { }

    public Guid UserId { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public byte[] Data { get; private set; } = [];

    /// <summary>Changes with every upload; clients put it in the image URL so the browser can cache the picture for a long time.</summary>
    public int Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static UserAvatar Create(Guid userId, string contentType, byte[] data, DateTimeOffset now) =>
        new() { UserId = userId, ContentType = contentType, Data = data, Version = 1, UpdatedAt = now };

    public void Replace(string contentType, byte[] data, DateTimeOffset now)
    {
        ContentType = contentType;
        Data = data;
        Version++;
        UpdatedAt = now;
    }
}
