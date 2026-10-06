using L = Diga.Core.Localization.AppText;
using System.Net;
using System.Text.RegularExpressions;

namespace Diga.Core.Dlna;

public sealed record DlnaDevice(string Id, string FriendlyName, string Manufacturer, string ModelName,
    Uri DescriptionUri, Uri ContentDirectoryControlUri, string ContentDirectoryServiceType)
{
    /// <summary>The network address the device answers on. Any device can call itself a DIGA; two with the same name differ in this.</summary>
    public string Address => DescriptionUri.Host;
}

/// <param name="Devices">The devices whose description could be read, by name.</param>
/// <param name="ResponderCount">
/// How many different addresses answered the search with a usable LOCATION, whether or not a description was then read from them.
/// More than one means that something besides the recorder answered, even when only one device is listed.
/// </param>
public sealed record DlnaDiscoveryResult(IReadOnlyList<DlnaDevice> Devices, int ResponderCount);

public static class DlnaDevicePreference
{
    /// <summary>The first device named like a DIGA recorder (by friendly name, then model), otherwise the first device in the list.</summary>
    public static DlnaDevice? Preferred(IReadOnlyList<DlnaDevice> devices) =>
        devices.FirstOrDefault(device => device.FriendlyName.Contains("DIGA", StringComparison.OrdinalIgnoreCase))
        ?? devices.FirstOrDefault(device => device.ModelName.Contains("DIGA", StringComparison.OrdinalIgnoreCase))
        ?? devices.FirstOrDefault();
}

public enum DlnaTransferStatus { OriginalAvailable, Unverified, Protected, ConvertedOnly, Unavailable }

public sealed record DlnaResource(Uri Uri, string ProtocolInfo, long? SizeBytes, TimeSpan? Duration,
    bool IsProtected, bool? IsConverted, string? Protection = null)
{
    public string? MimeType => ProtocolInfo.Split(':', 4) is { Length: 4 } fields ? fields[2] : null;
    public bool IsHttpResource => EndpointPolicy.IsHttp(Uri) &&
        (string.IsNullOrWhiteSpace(ProtocolInfo) || ProtocolInfo.StartsWith("http-get:", StringComparison.OrdinalIgnoreCase));
}

public sealed record DlnaObject(string Id, string ParentId, string Title, bool IsContainer,
    DateTimeOffset? RecordedAt, IReadOnlyList<DlnaResource> Resources)
{
    public DlnaResource? PreferredResource => IsContainer ? null : Resources
        .Where(r => r.IsHttpResource && !r.IsProtected && r.IsConverted != true)
        .OrderBy(r => r.IsConverted == false ? 0 : 1).FirstOrDefault();

    public DlnaTransferStatus TransferStatus => PreferredResource is { } preferred
        ? preferred.IsConverted == false ? DlnaTransferStatus.OriginalAvailable : DlnaTransferStatus.Unverified
        : Resources.Any(r => r.IsHttpResource && !r.IsProtected && r.IsConverted == true) ? DlnaTransferStatus.ConvertedOnly
        : Resources.Any(r => r.IsProtected) ? DlnaTransferStatus.Protected : DlnaTransferStatus.Unavailable;
}

/// <summary>Metadata is a recorder claim, not proof that a stream is bit-identical or downloadable.</summary>
public static class DlnaProtocolInfo
{
    public static DlnaResource Parse(Uri uri, string protocolInfo, long? sizeBytes = null,
        TimeSpan? duration = null, string? protection = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        protocolInfo ??= "";
        if (protocolInfo.Length > 8192 || protection?.Length > 1024)
            throw new InvalidDataException(L.T("Core.Dlna.Resource.FieldLimit"));
        bool isProtected = !string.IsNullOrWhiteSpace(protection) ||
            Regex.IsMatch(protocolInfo, "DTCP|DRM|PLAYREADY|WIDEVINE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool? converted = null;
        foreach (var token in protocolInfo.Split(':', 4).Last().Split(';'))
        {
            var pair = token.Split('=', 2, StringSplitOptions.TrimEntries);
            if (!pair[0].Equals("DLNA.ORG_CI", StringComparison.OrdinalIgnoreCase)) continue;
            // Conflicting or malformed conversion claims are never eligible as originals.
            if (pair.Length != 2 || pair[1] is not ("0" or "1")) { converted = true; break; }
            var value = pair[1] == "1";
            if (converted.HasValue && converted.Value != value) { converted = true; break; }
            converted = value;
        }
        return new(uri, protocolInfo, sizeBytes is >= 0 ? sizeBytes : null,
            duration >= TimeSpan.Zero ? duration : null, isProtected, converted, protection);
    }
}

/// <param name="Headers">The reply as it arrived.</param>
/// <param name="RemoteAddress">The source address of the reply. Its sender can write any address there.</param>
/// <param name="LocalAddress">The address of the network interface that received the reply; null when the transport does not know it.</param>
/// <param name="LocalMask">The subnet mask of that interface. With both known, a reply from outside the subnet is not accepted.</param>
public sealed record DlnaSsdpResponse(string Headers, IPAddress RemoteAddress, IPAddress? LocalAddress = null, IPAddress? LocalMask = null);

/// <summary>Injectable discovery transport; the default, <see cref="DlnaSsdpTransport"/>, uses local IPv4 SSDP only.</summary>
public interface IDlnaSsdpTransport
{
    Task<IReadOnlyList<DlnaSsdpResponse>> SearchAsync(TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>The same search sent to one address instead of to every device of the network. A transport that has no way to do that refuses.</summary>
    Task<IReadOnlyList<DlnaSsdpResponse>> SearchAsync(IPAddress address, TimeSpan duration, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
