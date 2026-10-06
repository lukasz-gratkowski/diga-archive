using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Diga.Core.Dlna;

namespace Diga.Tests;

[Collection("Localization culture")]
public sealed class DlnaResponseTests : IDisposable
{
    private static readonly DlnaDevice Device = new("uuid:response-fixture", "Response fixture", "Panasonic", "DMR-BS850",
        new("http://127.0.0.1:62742/device.xml"), new("http://127.0.0.1:62742/control"),
        "urn:schemas-upnp-org:service:ContentDirectory:1");
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

    public void Dispose() => CultureInfo.CurrentUICulture = _originalUiCulture;

    public static IEnumerable<object[]> EmptyResponses()
    {
        foreach (var language in new[] { "en-US", "pl-PL" })
        foreach (var descriptionRequest in new[] { false, true })
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.NoContent })
        foreach (var body in new[] { "", " \r\n\t " })
            yield return [language, descriptionRequest, status, body];
    }

    [Theory]
    [MemberData(nameof(EmptyResponses))]
    public async Task EmptySuccessfulResponseExplainsMissingMetadataInSelectedLanguage(
        string language, bool descriptionRequest, HttpStatusCode status, string body)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(status, body)));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RequestMetadataAsync(http, descriptionRequest));

        var expected = language == "pl-PL"
            ? $"Nagrywarka zwróciła pustą odpowiedź (HTTP {(int)status}) zamiast metadanych DLNA."
            : $"The recorder returned an empty response (HTTP {(int)status}) instead of DLNA metadata.";
        Assert.Equal(expected, error.Message);
    }

    public static IEnumerable<object[]> FailedResponses()
    {
        var responses = new (HttpStatusCode Status, string Body, string ContentType)[]
        {
            (HttpStatusCode.BadRequest, "", "text/xml"),
            (HttpStatusCode.PreconditionFailed, "", "text/xml"),
            (HttpStatusCode.ServiceUnavailable, " \r\n\t ", "text/xml"),
            (HttpStatusCode.Unauthorized, "Access denied", "text/plain"),
            (HttpStatusCode.Forbidden, "<!DOCTYPE html><html><body>Access denied</body></html>", "text/html"),
            (HttpStatusCode.InternalServerError, "<html><body>Recorder is busy", "text/html"),
            (HttpStatusCode.ServiceUnavailable, "<html><body>Recorder is busy</body></html>", "text/html")
        };
        foreach (var language in new[] { "en-US", "pl-PL" })
        foreach (var response in responses)
            yield return [language, response.Status, response.Body, response.ContentType];
    }

    [Theory]
    [MemberData(nameof(FailedResponses))]
    public async Task FailedHttpResponsePreservesStatusInsteadOfLeakingXmlParserError(
        string language, HttpStatusCode status, string body, string contentType)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(status, body, contentType)));

        // Both public metadata entry points use the shared response reader.
        foreach (var descriptionRequest in new[] { false, true })
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => RequestMetadataAsync(http, descriptionRequest));
            Assert.Equal(status, error.StatusCode);
            Assert.Equal(language == "pl-PL"
                ? $"Nagrywarka zwróciła kod HTTP {(int)status}."
                : $"The recorder returned HTTP {(int)status}.", error.Message);
        }
    }

    [Theory]
    [InlineData("en-US", HttpStatusCode.InternalServerError, "701")]
    [InlineData("pl-PL", HttpStatusCode.InternalServerError, "701")]
    [InlineData("en-US", HttpStatusCode.OK, "720")]
    [InlineData("pl-PL", HttpStatusCode.OK, "720")]
    public async Task ValidSoapFaultPreservesUpnpCodeAndRecorderDescription(
        string language, HttpStatusCode status, string code)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        const string description = "No such object & catalogue unavailable";
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace control = "urn:schemas-upnp-org:control-1-0";
        var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body",
            new XElement(soap + "Fault", new XElement("faultcode", "s:Client"),
                new XElement("faultstring", "UPnPError"), new XElement("detail",
                    new XElement(control + "UPnPError", new XElement("errorCode", code),
                        new XElement("errorDescription", description))))));
        using var http = new HttpClient(new DlnaTestHandler(_ => Response(status, envelope.ToString())));
        using var browser = new DlnaContentDirectoryClient(http);

        var error = await Assert.ThrowsAsync<DlnaBrowseException>(() => browser.BrowseAsync(Device));

        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(language == "pl-PL"
            ? $"Błąd usługi ContentDirectory nagrywarki {code}: {description}"
            : $"Recorder ContentDirectory error {code}: {description}", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" />")]
    public async Task EmptyResultWithZeroCountsIsAValidEmptyCatalogue(string result)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace service = Device.ContentDirectoryServiceType;
        var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body",
            new XElement(service + "BrowseResponse", new XElement("Result", result),
                new XElement("NumberReturned", 0), new XElement("TotalMatches", 0), new XElement("UpdateID", 1))));
        var requests = 0;
        using var http = new HttpClient(new DlnaTestHandler(_ =>
        {
            requests++;
            return Response(HttpStatusCode.OK, envelope.ToString());
        }));
        using var browser = new DlnaContentDirectoryClient(http);

        Assert.Empty(await browser.BrowseAsync(Device));
        Assert.Equal(1, requests);
    }

    private static async Task RequestMetadataAsync(HttpClient http, bool descriptionRequest)
    {
        if (descriptionRequest)
        {
            using var discovery = new DlnaDiscoveryService(http);
            await discovery.GetDeviceAsync(Device.DescriptionUri);
        }
        else
        {
            using var browser = new DlnaContentDirectoryClient(http);
            await browser.BrowseAsync(Device);
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string contentType = "text/xml")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
}
