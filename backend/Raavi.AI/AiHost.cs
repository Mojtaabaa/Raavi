using System.Net;
using System.Net.Sockets;

namespace Raavi.AI;

/// <summary>An AI provider call failed in a way the caller can act on. <see cref="Code"/> is a stable API error code.</summary>
public sealed class AiProviderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Which hosts a tenant's AI provider may use (SSRF guard). Configured globally; an empty list denies everything. Loopback, link-local
/// (cloud metadata), multicast and unspecified addresses are always refused, also after DNS resolution; private ranges are allowed for
/// allow-listed hosts because on-premises model servers live there.
/// </summary>
public sealed class AiHostPolicy(IEnumerable<string> allowedHosts)
{
    private readonly string[] _allowed = [.. allowedHosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0)];

    public bool IsConfigured => _allowed.Length > 0;

    /// <summary>Validates and normalises a base URL (trailing slash, no query or fragment). Returns null with <paramref name="code"/> set when refused.</summary>
    public Uri? TryValidate(string? url, out string? code)
    {
        code = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            code = "ai.url_invalid";
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (!_allowed.Any(a => a == host || (a.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(a[1..], StringComparison.Ordinal))))
        {
            code = "ai.host_not_allowed";
            return null;
        }

        if (IPAddress.TryParse(host, out var ip) && IsForbidden(ip))
        {
            code = "ai.host_forbidden";
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }

    public static bool IsForbidden(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return (b[0] == 169 && b[1] == 254) || b[0] >= 224 || b[0] == 0;
        }

        return false;
    }

    /// <summary>HTTP handler that re-checks every resolved address at connect time (defeats DNS rebinding) and never follows redirects.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var allowed = addresses.Where(a => !IsForbidden(a)).ToArray();
            if (allowed.Length == 0)
            {
                throw new AiProviderException("ai.host_forbidden", "The AI provider host resolves only to forbidden addresses.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
