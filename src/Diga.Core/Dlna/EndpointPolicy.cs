using L = Diga.Core.Localization.AppText;
using System.Net;
using System.Net.Sockets;

namespace Diga.Core.Dlna;

internal static class EndpointPolicy
{
    internal static bool IsHttp(Uri uri) => uri.IsAbsoluteUri &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && !string.IsNullOrEmpty(uri.Host);

    internal static void Validate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsHttp(uri) || uri.AbsoluteUri.Length > 8192)
            throw new InvalidDataException(L.T("Core.Dlna.Endpoint.Invalid"));
    }

    internal static Uri Resolve(Uri recorder, Uri baseUri, string relative)
    {
        if (!Uri.TryCreate(baseUri, relative, out var uri)) throw new InvalidDataException(L.T("Core.Dlna.Endpoint.InvalidSupplied"));
        SameHost(recorder, uri);
        return uri;
    }

    internal static void SameHost(Uri recorder, Uri endpoint)
    {
        Validate(recorder); Validate(endpoint);
        if (!recorder.IdnHost.Equals(endpoint.IdnHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Core.Dlna.Endpoint.DifferentHost"));
    }

    internal static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, Credentials = null, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(3),
        MaxResponseHeadersLength = 32,
        ConnectCallback = ConnectLocalAsync
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// The only way this library opens a connection to a recorder, for its catalogue and for its recordings alike: the name is
    /// resolved here, and only an address that <see cref="IsLocalAddress"/> accepts is dialled. A name that also resolves to an
    /// address on the internet is connected to through its local addresses alone.
    /// </summary>
    internal static async ValueTask<Stream> ConnectLocalAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var numeric) ? [numeric]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var local = addresses.Where(IsLocalAddress).ToArray();
        if (local.Length == 0) throw new HttpRequestException(L.T("Core.Dlna.Endpoint.LocalRequired"));
        Exception? failure = null;
        foreach (var address in local)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            // The next address is tried whatever went wrong with this one; only a cancellation ends the attempt.
            catch (Exception ex) { socket.Dispose(); if (ex is OperationCanceledException) throw; failure = ex; }
        }
        throw new HttpRequestException(L.T("Core.Dlna.Endpoint.Unreachable"), failure);
    }

    /// <summary>
    /// The rule that keeps this library on the local network: this computer itself, the private IPv4 ranges (10.x.x.x,
    /// 172.16-31.x.x, 192.168.x.x), link-local addresses (169.254.x.x, fe80::/10) and unique local IPv6 addresses (fc00::/7).
    /// Everything else, including the shared range of internet providers (100.64.x.x), is not contacted.
    /// </summary>
    internal static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254;
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC);
    }
}
