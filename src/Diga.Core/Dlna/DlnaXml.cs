using L = Diga.Core.Localization.AppText;
using System.Xml;
using System.Xml.Linq;

namespace Diga.Core.Dlna;

internal static class DlnaXml
{
    internal const int MaxResponseBytes = 4 * 1024 * 1024;

    internal static XDocument Parse(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaxResponseBytes, MaxCharactersFromEntities = 0 };
        // Check depth before materializing the tree; a byte bound alone does not bound recursion.
        using (var depth = XmlReader.Create(new StringReader(text), settings))
            while (depth.Read()) if (depth.Depth > 64) throw new InvalidDataException(L.T("Core.Dlna.Xml.DepthLimit"));
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    internal static string Child(XElement element, string localName) => element.Elements()
        .FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() ?? "";

    internal static string Field(XElement element, string localName, int maximum = 4096)
    {
        var value = Child(element, localName);
        return value.Length <= maximum ? value : throw new InvalidDataException(L.T("Core.Dlna.Xml.FieldLimit", localName));
    }

    internal static async Task<string> ReadAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await ReadCoreAsync(client, request, cancellationToken).ConfigureAwait(false); }
        // The exchange limit and the transport's connect timeout are a recorder failure, not a user cancellation.
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(L.T("Core.Dlna.Xml.Timeout"), ex); }
    }

    private static async Task<string> ReadCoreAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidDataException(L.T("Core.Dlna.Xml.RedirectRejected"));
        if (response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri)
            throw new InvalidDataException(L.T("Core.Dlna.Xml.UnexpectedRedirect"));
        if (response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException(L.T("Core.Dlna.Xml.EncodingUnsupported"));
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException(L.T("Core.Dlna.Xml.SizeLimit"));
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaxResponseBytes) throw new InvalidDataException(L.T("Core.Dlna.Xml.SizeLimit"));
            output.Write(buffer, 0, count);
        }
        output.Position = 0;
        using var text = new StreamReader(output, System.Text.Encoding.UTF8, true);
        var xml = await text.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
        {
            // SOAP faults commonly arrive as HTTP 500. Expose only their bounded code/description.
            // An empty or non-XML error body must not hide the recorder's HTTP status.
            XElement? fault = null;
            XmlException? parseError = null;
            if (!string.IsNullOrWhiteSpace(xml))
            {
                try { fault = Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError"); }
                catch (XmlException error) { parseError = error; }
            }
            if (fault is not null) throw new DlnaBrowseException(Child(fault, "errorCode"), Child(fault, "errorDescription"));
            throw new HttpRequestException(L.T("Core.Dlna.Xml.HttpError", (int)response.StatusCode), parseError, response.StatusCode);
        }
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidDataException(L.T("Core.Dlna.Xml.EmptyResponse", (int)response.StatusCode));
        return xml;
    }
}

public sealed class DlnaBrowseException(string errorCode, string description)
    : IOException(L.T("Core.Dlna.Browse.RemoteError", errorCode[..Math.Min(errorCode.Length, 32)], description[..Math.Min(description.Length, 512)]))
{
    public string ErrorCode { get; } = errorCode[..Math.Min(errorCode.Length, 32)];
}
