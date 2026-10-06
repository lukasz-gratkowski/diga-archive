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
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            var candidates = addresses.Where(IsLocalAddress).ToArray();
            if (candidates.Length == 0) throw new HttpRequestException(L.T("Core.Dlna.Endpoint.LocalRequired"));
            Exception? last = null;
            foreach (var address in candidates)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException ex) { last = ex; socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException(L.T("Core.Dlna.Endpoint.Unreachable"), last);
        }
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
              (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254)
            : address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
    }
}
