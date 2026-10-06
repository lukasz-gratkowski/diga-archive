using L = Diga.Core.Localization.AppText;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Diga.Core.Dlna;

public sealed class DlnaContentDirectoryClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeSpan _listingLimit;
    public DlnaContentDirectoryClient(HttpClient? client = null, TimeSpan? listingLimit = null)
    {
        _listingLimit = listingLimit ?? TimeSpan.FromMinutes(2);
        if (_listingLimit <= TimeSpan.Zero || _listingLimit > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(listingLimit));
        _http = client ?? EndpointPolicy.CreateClient(); _ownsClient = client is null;
    }

    public async Task<IReadOnlyList<DlnaObject>> BrowseAsync(DlnaDevice device, string objectId = "0", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device); ArgumentNullException.ThrowIfNull(objectId);
        cancellationToken.ThrowIfCancellationRequested();
        EndpointPolicy.SameHost(device.DescriptionUri, device.ContentDirectoryControlUri);
        if (!DlnaDiscoveryService.IsContentDirectory(device.ContentDirectoryServiceType) || objectId.Length > 4096)
            throw new InvalidDataException(L.T("Core.Dlna.Browse.IdentifierInvalid"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_listingLimit);
        var entries = new List<DlnaObject>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        long metadataCharacters = 0;
        uint start = 0;
        string? updateId = null;
        uint? knownTotal = null;
        bool totalUnknown = false;
        for (int page = 0; page < 200; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException(L.T("Core.Dlna.Xml.Timeout"));
            using var request = CreateBrowse(device, objectId, start);
            string text;
            try { text = await DlnaXml.ReadAsync(_http, request, deadline.Token).ConfigureAwait(false); }
            catch (DlnaBrowseException ex) when (ex.ErrorCode == "720" && totalUnknown && entries.Count > 0) { return entries; }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException(L.T("Core.Dlna.Xml.Timeout"), ex); }
            metadataCharacters += text.Length;
            if (metadataCharacters > 16 * 1024 * 1024) throw new InvalidDataException(L.T("Core.Dlna.Browse.MetadataBudget"));
            var envelope = DlnaXml.Parse(text);
            var fault = envelope.Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError");
            if (fault is not null)
            {
                var code = DlnaXml.Child(fault, "errorCode");
                if (code == "720" && totalUnknown && entries.Count > 0) return entries;
                throw new DlnaBrowseException(code, DlnaXml.Child(fault, "errorDescription"));
            }
            var result = envelope.Descendants().FirstOrDefault(e => e.Name.LocalName == "BrowseResponse")
                ?? throw new InvalidDataException(L.T("Core.Dlna.Browse.ResponseMissing"));
            uint number = Count(result, "NumberReturned"), total = Count(result, "TotalMatches");
            if (number > 10000 || total > 10000) throw new InvalidDataException(L.T("Core.Dlna.Browse.ObjectLimit"));
            if (total != 0)
            {
                if (knownTotal.HasValue && knownTotal != total)
                    throw new InvalidDataException(L.T("Core.Dlna.Browse.TotalChanged"));
                knownTotal = total;
            }
            var currentUpdate = DlnaXml.Child(result, "UpdateID");
            if (updateId is not null && currentUpdate != updateId) throw new InvalidDataException(L.T("Core.Dlna.Browse.FolderChanged"));
            updateId = currentUpdate;
            totalUnknown = !knownTotal.HasValue;
            var didlText = DlnaXml.Child(result, "Result");
            var objects = string.IsNullOrWhiteSpace(didlText) ? [] : ParseDidl(device, didlText);
            if (objects.Count != number) throw new InvalidDataException(L.T("Core.Dlna.Browse.CountMismatch"));
            if (knownTotal.HasValue && (long)start + number > knownTotal.Value)
                throw new InvalidDataException(L.T("Core.Dlna.Browse.TooManyObjects"));
            if (number == 0)
            {
                if (knownTotal.HasValue && start < knownTotal.Value) throw new InvalidDataException(L.T("Core.Dlna.Browse.Incomplete"));
                return entries;
            }
            foreach (var item in objects)
            {
                if (!identifiers.Add(item.Id)) throw new InvalidDataException(L.T("Core.Dlna.Browse.RepeatedObject"));
                entries.Add(item);
                if (entries.Count > 10000) throw new InvalidDataException(L.T("Core.Dlna.Browse.ObjectLimit"));
            }
            start = checked(start + number);
            if (knownTotal.HasValue && start == knownTotal.Value) return entries;
        }
        throw new InvalidDataException(L.T("Core.Dlna.Browse.PageLimit"));
    }

    private static HttpRequestMessage CreateBrowse(DlnaDevice device, string objectId, uint start)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace service = device.ContentDirectoryServiceType;
        // Some Panasonic firmware rejects the equivalent default namespace form with HTTP 412.
        // Keep the action explicitly prefixed and its arguments unqualified on every page.
        var action = new XElement(service + "Browse", new XAttribute(XNamespace.Xmlns + "u", service.NamespaceName),
            new XElement("ObjectID", objectId), new XElement("BrowseFlag", "BrowseDirectChildren"),
            new XElement("Filter", "*"), new XElement("StartingIndex", start), new XElement("RequestedCount", 100), new XElement("SortCriteria", ""));
        var envelope = new XDocument(new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", soap.NamespaceName),
            new XAttribute(soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"), new XElement(soap + "Body", action)));
        var request = new HttpRequestMessage(HttpMethod.Post, device.ContentDirectoryControlUri)
        { Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
        request.Headers.Add("SOAPAction", $"\"{device.ContentDirectoryServiceType}#Browse\"");
        return request;
    }

    private static uint Count(XElement response, string name) => uint.TryParse(DlnaXml.Child(response, name), NumberStyles.None,
        CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException(L.T("Core.Dlna.Browse.FieldInvalid", name));

    private static List<DlnaObject> ParseDidl(DlnaDevice device, string xml)
    {
        var document = DlnaXml.Parse(xml);
        if (document.Root?.Name.LocalName != "DIDL-Lite") throw new InvalidDataException(L.T("Core.Dlna.Browse.DidlInvalid"));
        var result = new List<DlnaObject>();
        foreach (var element in document.Root.Elements())
        {
            if (element.Name.LocalName is not ("item" or "container")) continue;
            string id = (string?)element.Attribute("id") ?? "";
            if (id.Length == 0 || id.Length > 4096) throw new InvalidDataException(L.T("Core.Dlna.Browse.ObjectIdInvalid"));
            var resources = new List<DlnaResource>();
            foreach (var resource in element.Elements().Where(e => e.Name.LocalName == "res"))
            {
                if (resources.Count >= 32) throw new InvalidDataException(L.T("Core.Dlna.Browse.ResourceLimit"));
                Uri uri;
                try { uri = EndpointPolicy.Resolve(device.DescriptionUri, device.DescriptionUri, resource.Value.Trim()); }
                catch (InvalidDataException) { continue; } // Keep the item, but never make an external/unsafe URL downloadable.
                var protocol = (string?)resource.Attribute("protocolInfo") ?? "";
                // size="0" means unknown, not an empty recording.
                var size = long.TryParse((string?)resource.Attribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes > 0 ? bytes : (long?)null;
                var duration = ParseDuration((string?)resource.Attribute("duration"));
                var parsed = DlnaProtocolInfo.Parse(uri, protocol, size, duration, (string?)resource.Attribute("protection"));
                // A reference that resolves to the description document itself (empty, "http:", "?"): never offer it, but keep a protection marker.
                if (RequestTarget(uri) == RequestTarget(device.DescriptionUri) && !parsed.IsProtected) continue;
                resources.Add(parsed);
            }
            var title = DlnaXml.Field(element, "title");
            var parentId = (string?)element.Attribute("parentID") ?? "";
            if (parentId.Length > 4096) throw new InvalidDataException(L.T("Core.Dlna.Browse.ParentIdLimit"));
            var date = DateTimeOffset.TryParse(DlnaXml.Field(element, "date", 128), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var recorded) ? recorded : (DateTimeOffset?)null;
            result.Add(new(id, parentId, title.Length == 0 ? id : title,
                element.Name.LocalName == "container", date, resources));
        }
        return result;
    }

    private static string RequestTarget(Uri uri) => uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length > 1 ? uri.Query : "");

    private static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(':');
        if (parts.Length != 3 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes >= 60 ||
            !decimal.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds is < 0 or >= 60)
            return null;
        var ticks = ((decimal)hours * 3600 + minutes * 60 + seconds) * TimeSpan.TicksPerSecond;
        return ticks <= TimeSpan.MaxValue.Ticks ? TimeSpan.FromTicks((long)ticks) : null;
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
