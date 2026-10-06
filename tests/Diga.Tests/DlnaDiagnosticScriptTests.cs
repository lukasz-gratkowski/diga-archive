using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Diga.Core.Dlna;
using Xunit.Abstractions;

namespace Diga.Tests;

/// <summary>Exercises the shipped diagnostic executable through Windows PowerShell 5.1, using loopback only.</summary>
[Trait("Category", "Integration")]
public sealed class DlnaDiagnosticScriptTests(ITestOutputHelper output)
{
    private const string Service = "urn:schemas-upnp-org:service:ContentDirectory:1";
    private const string PrivateTitle = "PRIVATE_RECORDING_TITLE_8C34";
    private const string PrivateUdn = "PRIVATE_DEVICE_UDN_91BF";
    private const string PrivateToken = "PRIVATE_URL_TOKEN_66EA";
    private const string PrivateObject = "PRIVATE_OBJECT_ID_F7C2";

    [Theory]
    [InlineData("urn:schemas-upnp-org:service:ContentDirectory:1", false)]
    [InlineData("urn:schemas-upnp-org:service:ContentDirectory:2", false)]
    [InlineData("urn:schemas-upnp-org:service:ContentDirectory:2", true)]
    public async Task CanonicalAppEquivalentMatchesRealClientAndSucceedsWithoutUnneededRetry(string serviceType, bool legacyTryCanonical)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var recorder = new DiagnosticRecorder(output, serviceType);
        using var run = await RunScriptAsync(recorder.DescriptionUri, legacyTryCanonical: legacyTryCanonical);

        Assert.Equal(0, run.ExitCode);
        var browse = Assert.Single(run.Requests, IsBrowse);
        Assert.Equal("AppEquivalent", Text(browse, "Variant"));
        Assert.Equal("u prefix with unqualified arguments", Text(browse.GetProperty("Request"), "BrowseNamespace"));
        Assert.Equal("AppEquivalentBrowseSucceeded", Text(run.Report.RootElement, "Outcome"));
        Assert.Equal(200, browse.GetProperty("StatusCode").GetInt32());
        Assert.Equal(1, browse.GetProperty("Browse").GetProperty("NumberReturned").GetInt32());
        Assert.True(browse.GetProperty("ReceivedBytes").GetInt32() > 0);
        Assert.Equal(2, recorder.Requests.Count);
        Assert.All(recorder.Requests, request => Assert.Contains(request.Method, new[] { "GET", "POST" }));
        Assert.DoesNotContain(recorder.Requests, request => request.Target.Contains("/recording", StringComparison.Ordinal));
        Assert.Equal(2, run.Report.RootElement.GetProperty("SchemaVersion").GetInt32());
        // .NET Framework decides this header itself; the report must say what was actually sent on the wire.
        // Same host:port answering HTTP/1.1: Keep-Alive on the first (description) request only.
        var description = Assert.Single(run.Requests, request => Text(request, "Stage") == "Description");
        Assert.Equal("Keep-Alive", Assert.Single(recorder.Requests, request => request.Method == "GET").Connection);
        Assert.Equal("keep-alive", Text(description.GetProperty("Request"), "ConnectionHeader"));
        Assert.Null(Assert.Single(recorder.Requests, request => request.Method == "POST").Connection);
        Assert.Equal("absent", Text(browse.GetProperty("Request"), "ConnectionHeader"));
        Assert.Equal("1.1", Text(description, "ResponseHttpVersion"));
        Assert.Equal("1.1", Text(browse, "ResponseHttpVersion"));
        Assert.True(run.Report.RootElement.GetProperty("Device").GetProperty("ControlPortMatchesDescription").GetBoolean());
        Assert.Equal(0, run.Report.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.Equal(2, run.Report.RootElement.GetProperty("Limits").GetProperty("MaximumBrowseRequests").GetInt32());
        Assert.Contains("Browse comparison: enabled", run.AllOutput);
        Assert.Equal("Calculated", Text(run.Report.RootElement, "ScriptHashStatus"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(FindScript()))).ToLowerInvariant(),
            Text(run.Report.RootElement, "ScriptSha256"));
        Assert.True(run.SawJsonInZip);
        Assert.True(run.SawReadmeInZip);
        AssertPrivateValuesAbsent(run.AllOutput);

        // Capture the real app request in memory, then compare the script's actual wire bytes and headers.
        byte[]? expectedBody = null;
        string? expectedAction = null, expectedContentType = null;
        using var http = new HttpClient(new DlnaTestHandler(request =>
        {
            expectedBody = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            expectedAction = Assert.Single(request.Headers.GetValues("SOAPAction"));
            expectedContentType = request.Content.Headers.ContentType!.ToString();
            return DlnaTestHandler.Xml(Envelope(Didl(recorder.BaseUri), serviceType));
        }));
        using var browser = new DlnaContentDirectoryClient(http);
        var device = new DlnaDevice("uuid:test", "Test recorder", "Panasonic", "Synthetic",
            recorder.DescriptionUri, new Uri(recorder.BaseUri, "control"), serviceType);
        Assert.Single(await browser.BrowseAsync(device));
        var actual = Assert.Single(recorder.Requests, request => request.Method == "POST");
        Assert.NotNull(expectedBody);
        Assert.Equal(expectedBody, actual.BodyBytes);
        Assert.Equal(expectedAction, actual.SoapAction);
        Assert.Equal(expectedContentType, actual.ContentType);
        AssertCanonicalBody(actual.Body, serviceType);
        Assert.DoesNotContain("Legacy comparison succeeded", run.AllOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(200)]
    public async Task EmptyHttpRepliesRetainStatusAndDistinctEmptyXmlCategory(int status)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var recorder = new DiagnosticRecorder(output) { BrowseReply = (_, _) => new(status, "", PrivateTitle) };
        using var run = await RunScriptAsync(recorder.DescriptionUri, appEquivalentOnly: true);

        Assert.Equal(2, run.ExitCode);
        var browse = Assert.Single(run.Requests, IsBrowse);
        Assert.Equal(status, browse.GetProperty("StatusCode").GetInt32());
        Assert.Equal(0, browse.GetProperty("ReceivedBytes").GetInt32());
        Assert.Equal("Empty", Text(browse.GetProperty("Xml"), "Status"));
        Assert.Equal("AppEquivalent", Text(browse, "Variant"));
        Assert.Equal(2, recorder.Requests.Count);
        AssertPrivateValuesAbsent(run.AllOutput);
    }

    [Fact]
    public async Task DifferentControlHostIsRejectedBeforeAnyControlConnection()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var recorder = new DiagnosticRecorder(output)
        {
            // The alternate name resolves to loopback too: any accidental connection is observable here.
            DescriptionBody = server => Description($"http://localhost:{server.BaseUri.Port}/control?token={PrivateToken}")
        };
        using var run = await RunScriptAsync(recorder.DescriptionUri);

        Assert.Equal(2, run.ExitCode);
        Assert.Equal("GET", Assert.Single(recorder.Requests).Method);
        Assert.DoesNotContain(run.Requests, IsBrowse);
        AssertPrivateValuesAbsent(run.AllOutput);
    }

    [Fact]
    public async Task ControlUrlOnAnotherPortIsReportedAndItsFirstRequestCarriesKeepAlive()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var control = new DiagnosticRecorder(output);
        await using var recorder = new DiagnosticRecorder(output)
        {
            DescriptionBody = _ => Description($"http://127.0.0.1:{control.BaseUri.Port}/control")
        };
        using var run = await RunScriptAsync(recorder.DescriptionUri, appEquivalentOnly: true);

        Assert.Equal(0, run.ExitCode);
        Assert.False(run.Report.RootElement.GetProperty("Device").GetProperty("ControlPortMatchesDescription").GetBoolean());
        var browse = Assert.Single(run.Requests, IsBrowse);
        Assert.Equal("keep-alive", Text(browse.GetProperty("Request"), "ConnectionHeader"));
        Assert.Equal("GET", Assert.Single(recorder.Requests).Method);
        Assert.Equal("Keep-Alive", Assert.Single(control.Requests).Connection);
    }

    [Fact]
    public async Task RefusedControlConnectionKeepsItsTransportStatusAndHasNoResponseFields()
    {
        if (!OperatingSystem.IsWindows()) return;
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); closedPort = ((IPEndPoint)probe.LocalEndpoint).Port; }
        await using var recorder = new DiagnosticRecorder(output)
        {
            DescriptionBody = _ => Description($"http://127.0.0.1:{closedPort}/control")
        };
        using var run = await RunScriptAsync(recorder.DescriptionUri, appEquivalentOnly: true);

        Assert.Equal(2, run.ExitCode);
        var browse = Assert.Single(run.Requests, IsBrowse);
        Assert.Equal("ConnectFailure", Text(browse, "TransportError"));
        Assert.Equal(JsonValueKind.Null, browse.GetProperty("StatusCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, browse.GetProperty("ResponseHttpVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, browse.GetProperty("Request").GetProperty("ConnectionHeader").ValueKind);
        Assert.Equal("GET", Assert.Single(recorder.Requests).Method);
    }

    [Fact]
    public async Task DeclarationOnlyDidlIsReportedSeparatelyFromValidSoapEnvelope()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var recorder = new DiagnosticRecorder(output)
        {
            BrowseReply = (_, _) => new(200, Envelope("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"))
        };
        using var run = await RunScriptAsync(recorder.DescriptionUri, appEquivalentOnly: true);

        Assert.Equal(2, run.ExitCode);
        var request = Assert.Single(run.Requests, IsBrowse);
        Assert.Equal(200, request.GetProperty("StatusCode").GetInt32());
        Assert.Equal("Valid", Text(request.GetProperty("Xml"), "Status"));
        var browse = request.GetProperty("Browse");
        Assert.Equal("DidlInvalid", Text(browse, "Status"));
        Assert.Equal("Invalid", Text(browse.GetProperty("Didl"), "Status"));
        Assert.Equal(JsonValueKind.Null, browse.GetProperty("Didl").GetProperty("RootElement").ValueKind);
        Assert.Equal(2, recorder.Requests.Count);
    }

    [Fact]
    public async Task PlainTextHttp403RetainsStatusWithoutLeakingServerText()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var recorder = new DiagnosticRecorder(output)
        {
            BrowseReply = (_, _) => new(403, "Access denied: " + PrivateTitle + " " + PrivateToken,
                ReasonPhrase: PrivateUdn, ContentType: "text/plain; charset=utf-8")
        };
        using var run = await RunScriptAsync(recorder.DescriptionUri, appEquivalentOnly: true);

        Assert.Equal(2, run.ExitCode);
        var request = Assert.Single(run.Requests, IsBrowse);
        Assert.Equal(403, request.GetProperty("StatusCode").GetInt32());
        Assert.Equal("Forbidden", Text(request, "ReasonPhrase"));
        Assert.Equal("InvalidXmlOrNonXml", Text(request, "BodyCategory"));
        Assert.Equal("Invalid", Text(request.GetProperty("Xml"), "Status"));
        Assert.True(request.GetProperty("ReceivedBytes").GetInt32() > 0);
        Assert.Equal(2, recorder.Requests.Count);
        AssertPrivateValuesAbsent(run.AllOutput);
    }

    [Fact]
    public async Task BrowseRedirectIsReportedWithoutFollowingLocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var recorder = new DiagnosticRecorder(output)
        {
            BrowseReply = (server, attempt) => attempt == 1
                ? new(302, PrivateTitle, Location: new Uri(server.BaseUri, "forbidden?token=" + PrivateToken).AbsoluteUri)
                : new(200, Envelope(Didl(server.BaseUri)))
        };
        using var run = await RunScriptAsync(recorder.DescriptionUri, appEquivalentOnly: true);

        Assert.Equal(2, run.ExitCode);
        var request = Assert.Single(run.Requests, IsBrowse);
        Assert.Equal(302, request.GetProperty("StatusCode").GetInt32());
        Assert.Equal("RedirectRejected", Text(request, "BodyCategory"));
        Assert.Equal(2, recorder.Requests.Count);
        Assert.DoesNotContain(recorder.Requests, received => received.Target.Contains("/forbidden", StringComparison.Ordinal));
        AssertPrivateValuesAbsent(run.AllOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalDtdIsRejectedInDescriptionAndNestedDidlWithoutFetchingEntity(bool nestedDidl)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var recorder = new DiagnosticRecorder(output);
        if (nestedDidl)
            recorder.BrowseReply = (server, _) => new(200, Envelope(
                $"<!DOCTYPE DIDL-Lite [<!ENTITY secret SYSTEM '{server.BaseUri}forbidden?token={PrivateToken}'>]>" +
                Didl(server.BaseUri).Replace(PrivateTitle, "&secret;", StringComparison.Ordinal)));
        else
            recorder.DescriptionBody = server =>
                $"<!DOCTYPE root [<!ENTITY secret SYSTEM '{server.BaseUri}forbidden?token={PrivateToken}'>]>" +
                Description("/control").Replace(PrivateTitle, "&secret;", StringComparison.Ordinal);

        using var run = await RunScriptAsync(recorder.DescriptionUri, appEquivalentOnly: true);
        Assert.Equal(2, run.ExitCode);
        Assert.Equal(nestedDidl ? 2 : 1, recorder.Requests.Count);
        Assert.DoesNotContain(recorder.Requests, request => request.Target.Contains("/forbidden", StringComparison.Ordinal));
        var failed = run.Requests.Last();
        var xml = nestedDidl ? failed.GetProperty("Browse").GetProperty("Didl") : failed.GetProperty("Xml");
        Assert.Equal("DtdProhibited", Text(xml, "Status"));
        AssertPrivateValuesAbsent(run.AllOutput);
    }

    [Theory]
    [InlineData(false, false, 200, 0, 2)] // Default invocation compares the legacy serialization after HTTP 412.
    [InlineData(false, false, 412, 2, 2)] // Preserve both failures if neither request variant succeeds.
    [InlineData(true, false, 200, 2, 1)]  // Explicit single-request opt-out.
    [InlineData(true, true, 200, 2, 1)]   // Opt-out also wins over an older launcher's opt-in flag.
    [InlineData(false, true, 200, 0, 2)]  // Earlier launchers remain compatible.
    public async Task LegacyComparisonDefaultsOnAfterCanonicalHttp412AndHasExplicitOptOut(
        bool appEquivalentOnly, bool legacyTryCanonical, int secondStatus, int exitCode, int browseCount)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var recorder = new DiagnosticRecorder(output, "urn:schemas-upnp-org:service:ContentDirectory:2")
        {
            BrowseReply = (server, attempt) => attempt == 1 ? new(412, "")
                : new(secondStatus, secondStatus == 200 ? Envelope(Didl(server.BaseUri), server.ServiceType) : "")
        };
        using var run = await RunScriptAsync(recorder.DescriptionUri, appEquivalentOnly, legacyTryCanonical);

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Equal(2, run.Report.RootElement.GetProperty("Device").GetProperty("ServiceVersion").GetInt32());
        Assert.Equal(browseCount, run.Report.RootElement.GetProperty("Limits").GetProperty("MaximumBrowseRequests").GetInt32());
        Assert.Contains(appEquivalentOnly ? "Browse comparison: disabled" : "Browse comparison: enabled", run.AllOutput);
        Assert.Equal(exitCode == 0 ? "LegacyComparisonSucceeded" : "BrowseFailed", Text(run.Report.RootElement, "Outcome"));
        var requests = run.Requests.Where(IsBrowse).ToArray();
        Assert.Equal(browseCount, requests.Length);
        Assert.Equal("AppEquivalent", Text(requests[0], "Variant"));
        Assert.Equal(412, requests[0].GetProperty("StatusCode").GetInt32());
        Assert.Equal("PreconditionFailed", Text(requests[0], "ReasonPhrase"));
        Assert.Equal(0, requests[0].GetProperty("ReceivedBytes").GetInt32());
        Assert.Equal("Empty", Text(requests[0].GetProperty("Xml"), "Status"));
        var actual = recorder.Requests.Where(request => request.Method == "POST").ToArray();
        Assert.Equal(browseCount, actual.Length);
        Assert.All(actual, request =>
        {
            Assert.Equal('"' + recorder.ServiceType + "#Browse\"", request.SoapAction);
            var action = XDocument.Parse(request.Body).Descendants().Single(element => element.Name.LocalName == "Browse");
            Assert.Equal(recorder.ServiceType, action.Name.NamespaceName);
        });
        AssertCanonicalBody(actual[0].Body, recorder.ServiceType);
        Assert.Equal("u prefix with unqualified arguments", Text(requests[0].GetProperty("Request"), "BrowseNamespace"));
        if (!appEquivalentOnly)
        {
            Assert.Equal("LegacyDefaultNamespace", Text(requests[1], "Variant"));
            Assert.Equal("default namespace with unqualified arguments", Text(requests[1].GetProperty("Request"), "BrowseNamespace"));
            Assert.Equal(secondStatus, requests[1].GetProperty("StatusCode").GetInt32());
            Assert.Contains("<Browse xmlns=\"" + recorder.ServiceType + "\">", actual[1].Body, StringComparison.Ordinal);
            Assert.Contains("xmlns=\"\"", actual[1].Body, StringComparison.Ordinal);
            Assert.DoesNotContain("<u:Browse", actual[1].Body, StringComparison.Ordinal);
            var legacyAction = XDocument.Parse(actual[1].Body).Descendants().Single(element => element.Name.LocalName == "Browse");
            Assert.All(legacyAction.Elements(), argument => Assert.Equal(XNamespace.None, argument.Name.Namespace));
            if (secondStatus == 200)
                Assert.Contains("Legacy comparison succeeded; the current app-equivalent Browse failed.", run.AllOutput);
            if (secondStatus == 412)
            {
                Assert.Equal("PreconditionFailed", Text(requests[1], "ReasonPhrase"));
                Assert.Equal(0, requests[1].GetProperty("ReceivedBytes").GetInt32());
                Assert.Equal("Empty", Text(requests[1].GetProperty("Xml"), "Status"));
            }
        }
        AssertPrivateValuesAbsent(run.AllOutput);
    }

    private static void AssertCanonicalBody(string body, string serviceType)
    {
        Assert.Contains("<u:Browse xmlns:u=\"" + serviceType + "\">", body, StringComparison.Ordinal);
        Assert.DoesNotContain("xmlns=\"\"", body, StringComparison.Ordinal);
        var action = XDocument.Parse(body).Descendants().Single(element => element.Name.LocalName == "Browse");
        Assert.Equal(serviceType, action.Name.NamespaceName);
        Assert.Equal(new[] { "ObjectID", "BrowseFlag", "Filter", "StartingIndex", "RequestedCount", "SortCriteria" },
            action.Elements().Select(argument => argument.Name.LocalName));
        Assert.Equal(new[] { "0", "BrowseDirectChildren", "*", "0", "100", "" },
            action.Elements().Select(argument => argument.Value));
        Assert.All(action.Elements(), argument => Assert.Equal(XNamespace.None, argument.Name.Namespace));
    }

    private static bool IsBrowse(JsonElement request) => Text(request, "Stage") == "Browse";
    private static string? Text(JsonElement value, string property) => value.GetProperty(property).GetString();
    private static void AssertPrivateValuesAbsent(string output)
    {
        foreach (var secret in new[] { PrivateTitle, PrivateUdn, PrivateToken, PrivateObject })
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
    }

    private static string Description(string controlUrl, string serviceType = Service)
    {
        var root = new XElement("root", new XElement("device",
            new XElement("deviceType", "urn:schemas-upnp-org:device:MediaServer:1"), new XElement("friendlyName", PrivateTitle),
            new XElement("manufacturer", "Panasonic"), new XElement("modelName", "DIGA test fixture"), new XElement("UDN", "uuid:" + PrivateUdn),
            new XElement("serviceList", new XElement("service", new XElement("serviceType", serviceType),
                new XElement("serviceId", "urn:upnp-org:serviceId:ContentDirectory"), new XElement("controlURL", controlUrl)))));
        foreach (var element in root.DescendantsAndSelf()) element.Name = XName.Get(element.Name.LocalName, "urn:schemas-upnp-org:device-1-0");
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static string Didl(Uri recorder)
    {
        XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        return new XElement(didl + "DIDL-Lite", new XElement(didl + "item", new XAttribute("id", PrivateObject), new XAttribute("parentID", "0"),
            new XAttribute("restricted", "1"), new XElement(dc + "title", PrivateTitle), new XElement(didl + "res",
                new XAttribute("protocolInfo", "http-get:*:video/mpeg:DLNA.ORG_CI=0"), new XAttribute("size", "123456"),
                new Uri(recorder, $"recording/{PrivateObject}?token={PrivateToken}").AbsoluteUri))).ToString(SaveOptions.DisableFormatting);
    }

    private static string Envelope(string didl, string serviceType = Service)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        return new XElement(soap + "Envelope", new XElement(soap + "Body", new XElement(XName.Get("BrowseResponse", serviceType),
            new XElement("Result", didl), new XElement("NumberReturned", 1), new XElement("TotalMatches", 1), new XElement("UpdateID", 1))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private async Task<ScriptRun> RunScriptAsync(Uri description, bool appEquivalentOnly = false, bool legacyTryCanonical = false)
    {
        var script = FindScript();
        var root = Path.Combine(Path.GetTempPath(), "Diga-diagnostic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-DescriptionUrl", description.AbsoluteUri, "-OutputDirectory", root, "-NonInteractive" }) start.ArgumentList.Add(argument);
            if (legacyTryCanonical) start.ArgumentList.Add("-TryCanonicalBrowse");
            if (appEquivalentOnly) start.ArgumentList.Add("-AppEquivalentOnly");
            var elapsed = Stopwatch.StartNew();
            using var process = Process.Start(start) ?? throw new IOException("Could not launch Windows PowerShell 5.1.");
            // Dedicated threads: an asynchronous read of a redirected pipe holds a thread-pool thread until the script exits.
            var stdout = Task.Factory.StartNew(process.StandardOutput.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var stderr = Task.Factory.StartNew(process.StandardError.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            var console = await stdout + "\n" + await stderr;
            var reports = Directory.GetFiles(root, "report.json", SearchOption.AllDirectories);
            Assert.True(reports.Length == 1, $"Expected one report, got {reports.Length}. Exit {process.ExitCode}. {console}");
            // Printed with a failed test: what the script saw on each request, and whether the test host could keep up.
            output.WriteLine($"Script exit code {process.ExitCode} after {elapsed.ElapsedMilliseconds} ms. Test host: {Environment.ProcessorCount} processors, " +
                $"{ThreadPool.ThreadCount} thread-pool threads, {ThreadPool.PendingWorkItemCount} queued work items.");
            using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(reports[0])))
                foreach (var request in summary.RootElement.GetProperty("Requests").EnumerateArray())
                    output.WriteLine($"  script {Text(request, "Stage")} {Text(request, "Variant")}: status {request.GetProperty("StatusCode").GetRawText()}, " +
                        $"transport error {Text(request, "TransportError") ?? "none"}, {request.GetProperty("ElapsedMilliseconds").GetRawText()} ms");
            var allOutput = new StringBuilder(console);
            foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                allOutput.AppendLine(Path.GetFileName(path));
                allOutput.AppendLine(await File.ReadAllTextAsync(path));
            }
            var zip = Assert.Single(Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories));
            bool sawJson = false, sawReadme = false;
            using (var archive = ZipFile.OpenRead(zip))
                foreach (var entry in archive.Entries)
                {
                    allOutput.AppendLine(entry.FullName);
                    if (entry.Name.Length == 0) continue;
                    sawJson |= entry.Name.Equals("report.json", StringComparison.OrdinalIgnoreCase);
                    sawReadme |= entry.Name.Equals("README.txt", StringComparison.OrdinalIgnoreCase);
                    using var reader = new StreamReader(entry.Open());
                    allOutput.AppendLine(await reader.ReadToEndAsync());
                }
            return new ScriptRun(root, process.ExitCode, JsonDocument.Parse(await File.ReadAllTextAsync(reports[0])),
                allOutput.ToString(), sawJson, sawReadme);
        }
        catch { DeleteOwnedDirectory(root); throw; }
    }

    private static void DeleteOwnedDirectory(string path)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        const string prefix = "Diga-diagnostic-tests-";
        var name = Path.GetFileName(target);
        if (!string.Equals(Directory.GetParent(target)?.FullName, temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Refusing to remove an unowned diagnostic test directory.");
        if (!Directory.Exists(target)) return;
        var directories = new Stack<string>();
        directories.Push(target);
        int entries = 0;
        while (directories.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to remove a diagnostic test reparse point.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 4096) throw new IOException("Diagnostic cleanup exceeds its bounded file budget.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing to remove a diagnostic test reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        }
        Directory.Delete(target, recursive: true);
    }

    private static string FindScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "Test-DlnaRecorder.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Cannot find scripts/Test-DlnaRecorder.ps1 above the test artifacts directory.");
    }

    private sealed record ScriptRun(string Root, int ExitCode, JsonDocument Report, string AllOutput, bool SawJsonInZip, bool SawReadmeInZip) : IDisposable
    {
        public JsonElement[] Requests => Report.RootElement.GetProperty("Requests").EnumerateArray().ToArray();
        public void Dispose() { Report.Dispose(); DeleteOwnedDirectory(Root); }
    }

    private sealed record Reply(int Status, string Body, string? ReasonPhrase = null, string? Location = null,
        string ContentType = "text/xml; charset=utf-8");
    private sealed record ReceivedRequest(string Method, string Target, string Body, byte[] BodyBytes, string? SoapAction, string? ContentType, string? Connection);

    private sealed class DiagnosticRecorder : IAsyncDisposable
    {
        private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly Thread _serve;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentQueue<string> _log = new();
        private readonly ITestOutputHelper _output;
        private volatile bool _stopping;
        private volatile Socket? _connection;
        private Exception? _failure;
        private int _browseCount;
        public Uri BaseUri { get; }
        public string ServiceType { get; }
        public Uri DescriptionUri => new(BaseUri, "description.xml?token=" + PrivateToken);
        public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();
        public ConcurrentQueue<Exception> ConnectionErrors { get; } = new();
        public Func<DiagnosticRecorder, string> DescriptionBody { get; set; } = server => Description("/control", server.ServiceType);
        public Func<DiagnosticRecorder, int, Reply> BrowseReply { get; set; } = (server, _) => new(200, Envelope(Didl(server.BaseUri), server.ServiceType));

        public DiagnosticRecorder(ITestOutputHelper output, string serviceType = Service)
        {
            _output = output;
            ServiceType = serviceType;
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndPoint!).Port}/");
            // Its own thread and blocking sockets. The script runs in another process against its ten-second request
            // timeout, so a reply must not wait for a free thread-pool thread in the test host: tests running in
            // parallel read their child processes' redirected pipes on pool threads and can leave none free for that
            // long (seen on hosted runners while ActualProcessCancellationTerminatesFfmpeg ran alongside).
            _serve = new Thread(Serve) { IsBackground = true, Name = nameof(DiagnosticRecorder) };
            _serve.Start();
        }

        private void Log(string text) => _log.Enqueue($"  recorder :{BaseUri.Port} +{_clock.ElapsedMilliseconds} ms: {text}");

        private void Serve()
        {
            try
            {
                while (true)
                {
                    using var connection = _listener.Accept();
                    _connection = connection;
                    if (_stopping) return;
                    try
                    {
                    connection.ReceiveTimeout = connection.SendTimeout = 15000;
                    using var stream = new NetworkStream(connection);
                    var bytes = new List<byte>();
                    var single = new byte[1];
                    while (bytes.Count < 16384)
                    {
                        if (stream.Read(single) == 0) throw new IOException($"Incomplete diagnostic test request ({bytes.Count} header bytes).");
                        bytes.Add(single[0]);
                        if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
                    }
                    if (bytes.Count >= 16384) throw new IOException("Oversized diagnostic test request headers.");
                    var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                    var parts = lines[0].Split(' ');
                    var lengthHeader = lines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var length = lengthHeader is null ? 0 : int.Parse(lengthHeader.Split(':')[1].Trim(), CultureInfo.InvariantCulture);
                    if (length is < 0 or > 16384) throw new IOException("Oversized diagnostic test request body.");
                    if (lines.Any(line => line.Equals("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)))
                        stream.Write("HTTP/1.1 100 Continue\r\n\r\n"u8);
                    var body = new byte[length];
                    stream.ReadExactly(body);
                    string? Header(string name) => lines.FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?
                        .Split(':', 2)[1].Trim();
                    Requests.Enqueue(new(parts[0], parts[1], Encoding.UTF8.GetString(body), body, Header("SOAPAction"), Header("Content-Type"), Header("Connection")));
                    var reply = parts[0] == "GET" && parts[1].StartsWith("/description.xml", StringComparison.Ordinal)
                        ? new Reply(200, DescriptionBody(this)) : BrowseReply(this, ++_browseCount);
                    var response = Encoding.UTF8.GetBytes(reply.Body);
                    var reason = reply.ReasonPhrase ?? (reply.Status == 200 ? "OK" : "Internal Server Error");
                    var location = reply.Location is null ? "" : $"Location: {reply.Location}\r\n";
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} {reason}\r\nContent-Type: {reply.ContentType}\r\nContent-Length: {response.Length}\r\n{location}Connection: close\r\n\r\n");
                    // One write: the script may close the connection as soon as it has the headers of an empty
                    // reply, and a second write on that connection would then fail.
                    stream.Write(header.Concat(response).ToArray());
                    Log($"{parts[0]} {parts[1].Split('?')[0]} answered with HTTP {reply.Status}");
                    }
                    // One broken connection (a client that connects and leaves, closes before the reply is written, or stalls)
                    // must not end the fixture: the requests that follow would hang until the script's own timeout.
                    catch (Exception ex) when ((ex is IOException or SocketException) && !_stopping)
                    {
                        ConnectionErrors.Enqueue(ex);
                        Log($"connection error {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            // Closing the listener or the current connection ends a blocking call with one of these.
            catch (Exception ex) when ((ex is IOException or SocketException or ObjectDisposedException) && _stopping) { }
            // Anything else (a reply delegate that throws, a request the fixture cannot parse) is rethrown by DisposeAsync.
            catch (Exception ex) { _failure = ex; Log($"recorder ended by {ex.GetType().Name}: {ex.Message}"); }
        }

        public ValueTask DisposeAsync()
        {
            _stopping = true;
            _listener.Dispose();
            _connection?.Dispose();
            var stopped = _serve.Join(TimeSpan.FromSeconds(5));
            // Printed with a failed test, next to what the script reported.
            foreach (var line in _log) _output.WriteLine(line);
            if (!stopped) throw new TimeoutException("The diagnostic test recorder did not stop.");
            if (_failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_failure);
            return ValueTask.CompletedTask;
        }
    }
}
