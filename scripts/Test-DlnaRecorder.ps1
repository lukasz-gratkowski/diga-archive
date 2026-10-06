#requires -Version 5.1
<#
.SYNOPSIS
Creates a sanitized, local diagnostic ZIP for a UPnP/DLNA recorder.
.DESCRIPTION
Uses only Windows PowerShell and .NET Framework. Performs SSDP discovery, reads
one selected device description and sends the app's canonical u:Browse request.
By default, compares the legacy default-namespace request only if it fails. Use
-AppEquivalentOnly to disable that comparison. It never downloads recordings
or changes recorder settings.
.PARAMETER DescriptionUrl
Optional HTTP(S) device-description URL using a private/link-local/loopback IP
literal. localhost is supported for local fixtures. No credentials or fragment.
.PARAMETER OutputDirectory
Parent directory for a new report folder and its ZIP. Existing files are not
overwritten. Defaults to a DlnaDiagnostics folder next to this script.
.PARAMETER NonInteractive
Never prompts. Supply DescriptionUrl when discovery finds zero/multiple devices.
.PARAMETER TryCanonicalBrowse
Accepted for compatibility with earlier launchers. The canonical u:Browse
request is always first; a legacy serialization comparison is enabled by default.
Use AppEquivalentOnly to disable the comparison.
.PARAMETER AppEquivalentOnly
Send only the app-equivalent canonical u:Browse, with no legacy comparison.
This takes precedence over TryCanonicalBrowse when both switches are supplied.
#>
[CmdletBinding()]
param(
    [string]$DescriptionUrl,
    [string]$OutputDirectory,
    [switch]$NonInteractive,
    [switch]$TryCanonicalBrowse,
    [switch]$AppEquivalentOnly,
    [ValidateRange(1, 10)][int]$DiscoverySeconds = 3,
    [ValidateRange(1, 30)][int]$TimeoutSeconds = 10
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
# Keep the old opt-in switch accepted, but give the explicit opt-out precedence.
$script:CompareSerialization = -not $AppEquivalentOnly
Add-Type -AssemblyName System.Xml.Linq
Add-Type -AssemblyName System.IO.Compression.FileSystem
$script:MaximumBytes = 4 * 1024 * 1024
$script:KnownElements = @('root', 'specVersion', 'major', 'minor', 'URLBase', 'device', 'deviceType',
    'friendlyName', 'manufacturer', 'manufacturerURL', 'modelDescription', 'modelName', 'modelNumber',
    'modelURL', 'serialNumber', 'UDN', 'UPC', 'iconList', 'icon', 'mimetype', 'width', 'height', 'depth',
    'url', 'serviceList', 'service', 'serviceType', 'serviceId', 'SCPDURL', 'controlURL', 'eventSubURL',
    'deviceList', 'presentationURL', 'Envelope', 'Header', 'Body', 'Fault', 'faultcode', 'faultstring',
    'detail', 'UPnPError', 'errorCode', 'errorDescription', 'BrowseResponse', 'Result', 'NumberReturned',
    'TotalMatches', 'UpdateID', 'DIDL-Lite', 'container', 'item', 'title', 'class', 'date', 'res',
    'creator', 'artist', 'album', 'genre', 'albumArtURI', 'description', 'longDescription', 'restricted')
$selfStream = $null
$hasher = $null
$scriptHash = $null
try {
    $selfStream = [IO.File]::OpenRead($PSCommandPath)
    $hasher = [Security.Cryptography.SHA256]::Create()
    $scriptHash = [BitConverter]::ToString($hasher.ComputeHash($selfStream)).Replace('-', '').ToLowerInvariant()
} catch { $scriptHash = $null }
finally { if ($hasher) { $hasher.Dispose() }; if ($selfStream) { $selfStream.Dispose() } }
$script:Report = [ordered]@{
    SchemaVersion = 2
    Tool ='AMG DIGA Archive DLNA diagnostic'
    ScriptSha256 = $scriptHash
    ScriptHashStatus = $(if ($scriptHash) { 'Calculated' } else { 'Unavailable' })
    Build = [ordered]@{ Version = $null; Commit = $null; ModifiedSources = $null; MetadataSource = 'Absent' }
    CreatedUtc = [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    Outcome = 'NotStarted'
    ExitCode = 3
    Privacy = 'Sanitized: no response bodies, endpoint URLs, device identifiers, recording titles or tokens are retained.'
    Limits = [ordered]@{ MaximumResponseBytes = $script:MaximumBytes; RequestTimeoutSeconds = $TimeoutSeconds; MaximumBrowseRequests = $(if ($script:CompareSerialization) { 2 } else { 1 }) }
    Discovery = [ordered]@{ Used = $false; ActiveIpv4Interfaces = 0; SocketFailures = 0; RepliesReceived = 0; CandidateLocations = 0; DescriptionsAttempted = 0; BudgetReached = $false }
    Device = [ordered]@{ ContentDirectoryFound = $false; ServiceVersion = $null; UrlBasePresent = $false; SameHostControl = $false; ControlPortMatchesDescription = $null }
    Requests = @()
    ErrorCategory = $null
}

# Copy only bounded, validated provenance fields; never arbitrary local JSON data.
foreach ($buildCandidate in @(
    [pscustomobject]@{ Path = (Join-Path $PSScriptRoot 'BUILDINFO.json'); Source = 'Adjacent' },
    [pscustomobject]@{ Path = (Join-Path (Split-Path $PSScriptRoot -Parent) 'BUILDINFO.json'); Source = 'Parent' }
)) {
    if (-not [IO.File]::Exists($buildCandidate.Path)) { continue }
    $buildStream = $null
    try {
        $buildStream = [IO.File]::OpenRead($buildCandidate.Path)
        if ($buildStream.Length -gt 16384) { continue }
        $buildBytes = New-Object byte[] 16385
        $buildLength = $buildStream.Read($buildBytes, 0, $buildBytes.Length)
        if ($buildLength -gt 16384) { continue }
        $buildText = [Text.Encoding]::UTF8.GetString($buildBytes, 0, $buildLength).TrimStart([char]0xfeff)
        $buildInfo = $buildText | ConvertFrom-Json
        if ($buildInfo.PSObject.Properties.Name -contains 'Version' -and $buildInfo.Version -is [string] -and $buildInfo.Version -cmatch '^[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}$') { $script:Report.Build.Version = $buildInfo.Version }
        if ($buildInfo.PSObject.Properties.Name -contains 'Commit' -and $buildInfo.Commit -is [string] -and $buildInfo.Commit -cmatch '^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$') { $script:Report.Build.Commit = $buildInfo.Commit.ToLowerInvariant() }
        if ($buildInfo.PSObject.Properties.Name -contains 'ModifiedSources' -and $buildInfo.ModifiedSources -is [bool]) { $script:Report.Build.ModifiedSources = $buildInfo.ModifiedSources }
        $script:Report.Build.MetadataSource = $buildCandidate.Source
        break
    } catch { continue }
    finally { if ($buildStream) { $buildStream.Dispose() } }
}

function Test-LocalAddress([Net.IPAddress]$Address) {
    if ([Net.IPAddress]::IsLoopback($Address)) { return $true }
    if ($Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
        $b = $Address.GetAddressBytes()
        return ($b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or
            ($b[0] -eq 192 -and $b[1] -eq 168) -or ($b[0] -eq 169 -and $b[1] -eq 254))
    }
    if ($Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6) {
        $b = $Address.GetAddressBytes()
        return ($Address.IsIPv6LinkLocal -or (($b[0] -band 0xfe) -eq 0xfc))
    }
    return $false
}

function Get-SafeEndpoint([string]$Value, [Uri]$ExpectedHost) {
    $uri = $null
    if ($Value.Length -gt 8192 -or -not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) { throw 'InvalidEndpoint' }
    if ($uri.Scheme -notin @('http', 'https') -or $uri.UserInfo -or $uri.Fragment -or $Value -match '[\r\n]') { throw 'InvalidEndpoint' }
    if ($ExpectedHost -and -not $uri.DnsSafeHost.Equals($ExpectedHost.DnsSafeHost, [StringComparison]::OrdinalIgnoreCase)) { throw 'CrossHostEndpointRejected' }
    if ($uri.DnsSafeHost -ne 'localhost') {
        $address = $null
        if (-not [Net.IPAddress]::TryParse($uri.DnsSafeHost, [ref]$address)) { throw 'UseRecorderIpAddress' }
        if (-not (Test-LocalAddress $address)) { throw 'NonLocalEndpointRejected' }
    }
    return $uri
}

function Get-XmlAnalysis([string]$Value) {
    $summary = [ordered]@{ Status = 'Invalid'; RootElement = $null; ElementCounts = [ordered]@{}; OtherElementCount = 0; ErrorLine = $null; ErrorPosition = $null }
    if ([string]::IsNullOrWhiteSpace($Value)) {
        $summary.Status = 'Empty'
        return [pscustomobject]@{ Summary = $summary; Document = $null }
    }
    # Do not expose an XmlException message: it may contain private input text.
    if ($Value -match '<!DOCTYPE') {
        $summary.Status = 'DtdProhibited'
        return [pscustomobject]@{ Summary = $summary; Document = $null }
    }
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = $script:MaximumBytes
    $reader = $null
    $source = New-Object IO.StringReader($Value)
    try {
        $reader = [Xml.XmlReader]::Create($source, $settings)
        while ($reader.Read()) { if ($reader.Depth -gt 64) { throw 'XmlDepthLimit' } }
        $reader.Dispose()
        $source.Dispose()
        $source = New-Object IO.StringReader($Value)
        $reader = [Xml.XmlReader]::Create($source, $settings)
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
        $summary.Status = 'Valid'
        $rootName = $document.DocumentElement.LocalName
        $summary.RootElement = $(if ($script:KnownElements -ccontains $rootName) { $rootName } else { '[other]' })
        foreach ($element in $document.SelectNodes('//*')) {
            $name = $element.LocalName
            if ($script:KnownElements -ccontains $name) {
                if (-not $summary.ElementCounts.Contains($name)) { $summary.ElementCounts[$name] = 0 }
                $summary.ElementCounts[$name]++
            } else { $summary.OtherElementCount++ }
        }
        return [pscustomobject]@{ Summary = $summary; Document = $document }
    } catch {
        $exception = $_.Exception
        while ($exception.InnerException) { $exception = $exception.InnerException }
        if ($exception -is [Xml.XmlException]) {
            $summary.ErrorLine = $exception.LineNumber
            $summary.ErrorPosition = $exception.LinePosition
        }
        if ($exception.Message -eq 'XmlDepthLimit') { $summary.Status = 'DepthLimit' }
        return [pscustomobject]@{ Summary = $summary; Document = $null }
    } finally {
        if ($reader) { $reader.Dispose() }
        $source.Dispose()
    }
}

function Get-ChildValue([Xml.XmlNode]$Node, [string]$Name) {
    foreach ($child in $Node.ChildNodes) { if ($child.LocalName -ceq $Name) { return $child.InnerText.Trim() } }
    return ''
}

function Get-WireCount([string]$Value) {
    $count = [uint32]0
    if ([uint32]::TryParse($Value, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$count)) { return [long]$count }
    return $null
}

function Get-SafeResponseHeaders([Net.HttpWebResponse]$Response) {
    $headers = [ordered]@{}
    $contentType = $Response.ContentType
    # Store allowlisted protocol values, never arbitrary header text/parameters.
    $mime = ($contentType -split ';', 2)[0].Trim().ToLowerInvariant()
    $headers['Content-Type'] = $(if ($mime -in @('text/xml', 'application/xml', 'application/soap+xml', 'text/html', 'text/plain', 'application/octet-stream', '')) { $mime } else { '[other]' })
    $headers['Charset'] = $(if ($contentType -match '(?i)charset\s*=\s*"?(utf-8|utf-16|us-ascii)\b') { $Matches[1].ToLowerInvariant() } elseif ($contentType -match '(?i)charset\s*=') { '[other]' } else { $null })
    foreach ($header in @('Content-Encoding', 'Transfer-Encoding', 'Connection')) {
        $value = $Response.Headers[$header]
        if ($value) {
            $allowed = @('identity', 'gzip', 'deflate', 'br', 'chunked', 'close', 'keep-alive')
            $tokens = @($value.ToLowerInvariant().Split(',') | ForEach-Object { $_.Trim() })
            $headers[$header] = $(if (@($tokens | Where-Object { $_ -notin $allowed }).Count -eq 0) { $tokens -join ', ' } else { '[other]' })
        }
    }
    foreach ($header in @('Location', 'WWW-Authenticate', 'Set-Cookie', 'Server')) { $headers[$header + '-Present'] = [bool]$Response.Headers[$header] }
    return $headers
}

function Invoke-DiagnosticRequest([Uri]$Uri, [string]$Stage, [string]$Variant, [byte[]]$Body, [string]$ServiceType) {
    $summary = [ordered]@{
        Stage = $Stage; Variant = $Variant; Method = $(if ($Body) { 'POST' } else { 'GET' })
        Request = [ordered]@{ HttpVersion = '1.1'; UserAgent = 'absent (matches app)'; XmlDeclaration = $false; BrowseNamespace = $(if ($Variant -eq 'AppEquivalent') { 'u prefix with unqualified arguments' } elseif ($Variant -eq 'LegacyDefaultNamespace') { 'default namespace with unqualified arguments' } else { $null }); RequestedCount = $(if ($Body) { 100 } else { $null }); ContentBytes = $(if ($Body) { $Body.Length } else { 0 }); ConnectionHeader = $null }
        StatusCode = $null; ResponseHttpVersion = $null; ReasonPhrase = $null; ReasonPhraseSource = 'standard HTTP status name (server-supplied text omitted)'
        Headers = [ordered]@{}; DeclaredBytes = $null; ReceivedBytes = 0; BodyCategory = 'Unavailable'
        Xml = $null; Browse = $null; TransportError = $null; ElapsedMilliseconds = 0
    }
    $response = $null
    $stream = $null
    $request = $null
    $memory = New-Object IO.MemoryStream
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $content = ''
    try {
        [void](Get-SafeEndpoint $Uri.AbsoluteUri $null)
        $transportUri = $Uri
        # Pin the only accepted hostname; all other endpoints must be IP literals.
        if ($Uri.DnsSafeHost -eq 'localhost') {
            $builder = New-Object UriBuilder($Uri)
            $builder.Host = '127.0.0.1'
            $transportUri = $builder.Uri
        }
        $request = [Net.HttpWebRequest]::Create($transportUri)
        $request.Method = $summary.Method
        if ($Uri.DnsSafeHost -eq 'localhost') { $request.Host = $Uri.Authority }
        $request.Proxy = $null
        $request.AllowAutoRedirect = $false
        $request.UseDefaultCredentials = $false
        $request.Credentials = $null
        $request.PreAuthenticate = $false
        $request.CookieContainer = $null
        $request.AutomaticDecompression = [Net.DecompressionMethods]::None
        $request.Timeout = $TimeoutSeconds * 1000
        $request.ReadWriteTimeout = $TimeoutSeconds * 1000
        $request.MaximumResponseHeadersLength = 32
        $request.ProtocolVersion = [Version]'1.1'
        $request.ServicePoint.Expect100Continue = $false
        $request.SendChunked = $false
        if ($Body) {
            $request.ContentType = 'text/xml; charset=utf-8'
            $request.ContentLength = $Body.Length
            $request.Headers.Add('SOAPAction', '"' + $ServiceType + '#Browse"')
            $outStream = $request.GetRequestStream()
            try { $outStream.Write($Body, 0, $Body.Length) } finally { $outStream.Dispose() }
        }
        try { $response = [Net.HttpWebResponse]$request.GetResponse() }
        catch [Net.WebException] {
            if ($_.Exception.Response) { $response = [Net.HttpWebResponse]$_.Exception.Response }
            else {
                $summary.TransportError = $_.Exception.Status.ToString()
                # No response: record the header only when .NET Framework had already added it to this request.
                $sentConnection = $request.Headers['Connection']
                if ($sentConnection) { $summary.Request.ConnectionHeader = $(if ($sentConnection -in @('Keep-Alive', 'Close')) { $sentConnection.ToLowerInvariant() } else { '[other]' }) }
                throw
            }
        }
        # .NET Framework adds Connection: Keep-Alive by itself (until a first HTTP/1.1 response from that host:port, for an HTTP/1.0 server, and sometimes again after a timeout); record it.
        $sentConnection = $request.Headers['Connection']
        $summary.Request.ConnectionHeader = $(if (-not $sentConnection) { 'absent' } elseif ($sentConnection -in @('Keep-Alive', 'Close')) { $sentConnection.ToLowerInvariant() } else { '[other]' })
        $summary.ResponseHttpVersion = $(if ($response.ProtocolVersion.ToString() -in @('1.0', '1.1')) { $response.ProtocolVersion.ToString() } else { '[other]' })
        $summary.StatusCode = [int]$response.StatusCode
        $summary.ReasonPhrase = $response.StatusCode.ToString()
        if ($summary.ReasonPhrase -match '^\d+$') { $summary.ReasonPhrase = 'UnrecognizedStatus' }
        $summary.Headers = Get-SafeResponseHeaders $response
        if ($response.ContentLength -ge 0) { $summary.DeclaredBytes = $response.ContentLength }
        if ([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400) {
            $summary.BodyCategory = 'RedirectRejected'
        } elseif ($response.Headers['Content-Encoding']) {
            $summary.BodyCategory = 'UnsupportedEncoding'
        } elseif ($response.ContentLength -gt $script:MaximumBytes) {
            $summary.BodyCategory = 'SizeLimit'
        } else {
            $stream = $response.GetResponseStream()
            $buffer = New-Object byte[] 16384
            while ($true) {
                $remaining = ($TimeoutSeconds * 1000) - [int]$watch.ElapsedMilliseconds
                if ($remaining -le 0) { $summary.TransportError = 'Timeout'; throw 'RequestTimeout' }
                if ($stream.CanTimeout) { $stream.ReadTimeout = $remaining }
                $count = $stream.Read($buffer, 0, [Math]::Min($buffer.Length, $script:MaximumBytes + 1 - [int]$memory.Length))
                if ($count -eq 0) { break }
                $memory.Write($buffer, 0, $count)
                $summary.ReceivedBytes = $memory.Length
                if ($memory.Length -gt $script:MaximumBytes) { $summary.BodyCategory = 'SizeLimit'; break }
            }
            if ($summary.BodyCategory -ne 'SizeLimit') {
                if ($null -ne $summary.DeclaredBytes -and $memory.Length -ne $summary.DeclaredBytes) { $summary.BodyCategory = 'Incomplete' }
                else {
                    $memory.Position = 0
                    $decoder = New-Object IO.StreamReader($memory, [Text.Encoding]::UTF8, $true, 1024, $true)
                    try { $content = $decoder.ReadToEnd() } finally { $decoder.Dispose() }
                    $analysis = Get-XmlAnalysis $content
                    $summary.Xml = $analysis.Summary
                    $summary.BodyCategory = $(if ($memory.Length -eq 0) { 'Empty' } elseif ([string]::IsNullOrWhiteSpace($content)) { 'WhitespaceOnly' } elseif ($analysis.Summary.Status -eq 'Valid') { 'Xml' } else { 'InvalidXmlOrNonXml' })
                }
            }
        }
    } catch {
        if (-not $summary.TransportError) {
            if ($summary.StatusCode -and $summary.ReceivedBytes -gt 0) { $summary.TransportError = 'ResponseReadFailed' }
            else {
                $failure = $_.Exception
                while ($failure -isnot [Net.WebException] -and $failure.InnerException) { $failure = $failure.InnerException }
                if ($failure -is [Net.WebException]) { $summary.TransportError = $failure.Status.ToString() } else { $summary.TransportError = 'RequestFailed' }
            }
        }
        if ($summary.BodyCategory -eq 'Unavailable' -and $summary.ReceivedBytes -gt 0) { $summary.BodyCategory = 'Incomplete' }
    } finally {
        if ($stream) { $stream.Dispose() }
        if ($response) { $response.Close() }
        if ($request) { $request.Abort() }
        $memory.Dispose()
        $summary.ElapsedMilliseconds = $watch.ElapsedMilliseconds
    }
    # Content remains only in process memory and is never added to the report.
    return [pscustomobject]@{ Summary = $summary; Content = $content }
}

function Get-Description([Uri]$Uri) {
    $exchange = Invoke-DiagnosticRequest $Uri 'Description' 'Description' $null $null
    $script:Report.Requests += $exchange.Summary
    if ($exchange.Summary.StatusCode -lt 200 -or $exchange.Summary.StatusCode -ge 300 -or $exchange.Summary.TransportError) { throw 'DescriptionHttpFailed' }
    $analysis = Get-XmlAnalysis $exchange.Content
    if ($analysis.Summary.Status -ne 'Valid') { throw 'DescriptionXmlInvalid' }
    $document = $analysis.Document
    $root = $document.DocumentElement
    if ($root.LocalName -cne 'root') { throw 'DescriptionRootInvalid' }
    $baseText = Get-ChildValue $root 'URLBase'
    $baseUri = $Uri
    if ($baseText) {
        $resolved = New-Object Uri($Uri, $baseText)
        $baseUri = Get-SafeEndpoint $resolved.AbsoluteUri $Uri
    }
    foreach ($service in $document.SelectNodes('//*[local-name()="service"]')) {
        $type = Get-ChildValue $service 'serviceType'
        if ($type -cmatch '^urn:schemas-upnp-org:service:ContentDirectory:([1-9][0-9]?)$') {
            $version = [int]$Matches[1]
            $controlText = Get-ChildValue $service 'controlURL'
            if (-not $controlText) { throw 'ControlUrlMissing' }
            $control = New-Object Uri($baseUri, $controlText)
            $control = Get-SafeEndpoint $control.AbsoluteUri $Uri
            $device = $service.ParentNode
            while ($device -and $device.LocalName -cne 'device') { $device = $device.ParentNode }
            if (-not $device) { throw 'DeviceElementMissing' }
            $friendly = Get-ChildValue $device 'friendlyName'
            return [pscustomobject]@{ DescriptionUri = $Uri; ControlUri = $control; ServiceType = $type; Version = $version; UrlBasePresent = [bool]$baseText; FriendlyName = $friendly }
        }
    }
    throw 'ContentDirectoryMissing'
}

function Get-BrowseBody([string]$ServiceType, [bool]$Canonical) {
    # XLinq reproduces the app's serialization: u:Browse when $Canonical (0.4.3 and later), otherwise the pre-0.4.3 default-namespace form.
    $soap = [Xml.Linq.XNamespace]::Get('http://schemas.xmlsoap.org/soap/envelope/')
    $service = [Xml.Linq.XNamespace]::Get($ServiceType)
    $action = New-Object Xml.Linq.XElement($service.GetName('Browse'))
    if ($Canonical) { $action.Add((New-Object Xml.Linq.XAttribute([Xml.Linq.XNamespace]::Xmlns.GetName('u'), $ServiceType))) }
    $arguments = [ordered]@{ ObjectID = '0'; BrowseFlag = 'BrowseDirectChildren'; Filter = '*'; StartingIndex = '0'; RequestedCount = '100'; SortCriteria = '' }
    foreach ($entry in $arguments.GetEnumerator()) { $action.Add((New-Object Xml.Linq.XElement([Xml.Linq.XName]::Get($entry.Key), [string]$entry.Value))) }
    $envelope = New-Object Xml.Linq.XElement($soap.GetName('Envelope'))
    $envelope.Add((New-Object Xml.Linq.XAttribute([Xml.Linq.XNamespace]::Xmlns.GetName('s'), $soap.NamespaceName)))
    $envelope.Add((New-Object Xml.Linq.XAttribute($soap.GetName('encodingStyle'), 'http://schemas.xmlsoap.org/soap/encoding/')))
    $body = New-Object Xml.Linq.XElement($soap.GetName('Body'))
    $body.Add($action)
    $envelope.Add($body)
    return ,([Text.Encoding]::UTF8.GetBytes($envelope.ToString([Xml.Linq.SaveOptions]::DisableFormatting)))
}

function Invoke-RootBrowse($Device, [bool]$Canonical) {
    $variant = $(if ($Canonical) { 'AppEquivalent' } else { 'LegacyDefaultNamespace' })
    $bytes = Get-BrowseBody $Device.ServiceType $Canonical
    $exchange = Invoke-DiagnosticRequest $Device.ControlUri 'Browse' $variant $bytes $Device.ServiceType
    $result = [ordered]@{ Status = 'Failed'; NumberReturned = $null; TotalMatches = $null; UpdateIdPresent = $false; SoapErrorCode = $null; SoapErrorDescriptionPresent = $false; ResultRepresentation = 'Absent'; Didl = $null; ActualObjectCount = $null; CountMatches = $null }
    $exchange.Summary.Browse = $result
    $script:Report.Requests += $exchange.Summary
    $analysis = Get-XmlAnalysis $exchange.Content
    if ($analysis.Summary.Status -ne 'Valid') { $result.Status = 'EnvelopeInvalid'; return $false }
    $document = $analysis.Document
    $fault = $document.SelectSingleNode('//*[local-name()="UPnPError"]')
    if ($fault) {
        $code = Get-ChildValue $fault 'errorCode'
        if ($code -match '^[0-9]{1,6}$') { $result.SoapErrorCode = $code } else { $result.SoapErrorCode = '[invalid]' }
        $result.SoapErrorDescriptionPresent = [bool](Get-ChildValue $fault 'errorDescription')
        $result.Status = 'SoapFault'
        return $false
    }
    if ($exchange.Summary.StatusCode -lt 200 -or $exchange.Summary.StatusCode -ge 300 -or $exchange.Summary.TransportError) { $result.Status = 'HttpFailed'; return $false }
    if ($document.DocumentElement.LocalName -cne 'Envelope') { $result.Status = 'EnvelopeInvalid'; return $false }
    $browse = $document.SelectSingleNode('//*[local-name()="BrowseResponse"]')
    if (-not $browse) { $result.Status = 'BrowseResponseMissing'; return $false }
    $result.NumberReturned = Get-WireCount (Get-ChildValue $browse 'NumberReturned')
    $result.TotalMatches = Get-WireCount (Get-ChildValue $browse 'TotalMatches')
    $result.UpdateIdPresent = [bool](Get-ChildValue $browse 'UpdateID')
    $resultNode = $browse.SelectSingleNode('*[local-name()="Result"]')
    $didlText = ''
    if ($resultNode) {
        $didlText = $resultNode.InnerText.Trim()
        $hasElements = @($resultNode.ChildNodes | Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element }).Count -gt 0
        $result.ResultRepresentation = $(if ($hasElements) { 'NestedElements (app expects escaped text)' } elseif ([string]::IsNullOrWhiteSpace($didlText)) { 'Empty' } else { 'EscapedTextOrCData' })
    }
    $didl = Get-XmlAnalysis $didlText
    $result.Didl = $didl.Summary
    if ([string]::IsNullOrWhiteSpace($didlText)) { $result.ActualObjectCount = 0 }
    elseif ($didl.Summary.Status -eq 'Valid' -and $didl.Document.DocumentElement.LocalName -ceq 'DIDL-Lite') {
        $result.ActualObjectCount = @($didl.Document.DocumentElement.ChildNodes | Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element -and $_.LocalName -cin @('container', 'item') }).Count
    } else { $result.Status = 'DidlInvalid'; return $false }
    if ($null -eq $result.NumberReturned -or $null -eq $result.TotalMatches) { $result.Status = 'CountsInvalid'; return $false }
    $result.CountMatches = $result.ActualObjectCount -eq $result.NumberReturned
    if (-not $result.CountMatches) { $result.Status = 'CountMismatch'; return $false }
    if (($result.NumberReturned -eq 0 -and $result.TotalMatches -gt 0) -or ($result.TotalMatches -ne 0 -and $result.NumberReturned -gt $result.TotalMatches)) { $result.Status = 'CountsInconsistent'; return $false }
    $result.Status = 'Success'
    return $true
}

function Find-DescriptionLocations {
    $script:Report.Discovery.Used = $true
    $sockets = New-Object 'Collections.Generic.List[Net.Sockets.UdpClient]'
    $locations = New-Object 'Collections.Generic.List[Uri]'
    $seen = New-Object 'Collections.Generic.HashSet[string]'
    $addresses = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        Where-Object { $_.OperationalStatus -eq [Net.NetworkInformation.OperationalStatus]::Up -and $_.SupportsMulticast -and $_.NetworkInterfaceType -ne [Net.NetworkInformation.NetworkInterfaceType]::Loopback } |
        ForEach-Object { $_.GetIPProperties().UnicastAddresses } |
        ForEach-Object { $_.Address } | Where-Object { $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and -not [Net.IPAddress]::IsLoopback($_) } |
        Sort-Object -Property IPAddressToString -Unique)
    $script:Report.Discovery.ActiveIpv4Interfaces = $addresses.Count
    try {
        foreach ($address in $addresses) {
            try {
                $socket = New-Object Net.Sockets.UdpClient((New-Object Net.IPEndPoint($address, 0)))
                $socket.Client.SetSocketOption([Net.Sockets.SocketOptionLevel]::IP, [Net.Sockets.SocketOptionName]::MulticastTimeToLive, 1)
                $socket.Client.SetSocketOption([Net.Sockets.SocketOptionLevel]::IP, [Net.Sockets.SocketOptionName]::MulticastInterface, $address.GetAddressBytes())
                $sockets.Add($socket)
                $target = New-Object Net.IPEndPoint([Net.IPAddress]::Parse('239.255.255.250'), 1900)
                foreach ($st in @('urn:schemas-upnp-org:device:MediaServer:1', 'urn:schemas-upnp-org:service:ContentDirectory:1')) {
                    $payload = [Text.Encoding]::ASCII.GetBytes("M-SEARCH * HTTP/1.1`r`nHOST: 239.255.255.250:1900`r`nMAN: `"ssdp:discover`"`r`nMX: 2`r`nST: $st`r`n`r`n")
                    [void]$socket.Send($payload, $payload.Length, $target)
                }
            } catch { $script:Report.Discovery.SocketFailures++ }
        }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.Elapsed.TotalSeconds -lt $DiscoverySeconds -and $script:Report.Discovery.RepliesReceived -lt 256) {
            foreach ($socket in $sockets) {
                if ($script:Report.Discovery.RepliesReceived -ge 256) { break }
                try {
                    if ($socket.Available -eq 0) { continue }
                    $remote = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
                    $data = $socket.Receive([ref]$remote)
                    $script:Report.Discovery.RepliesReceived++
                    if ($data.Length -gt 16384 -or -not (Test-LocalAddress $remote.Address)) { continue }
                    $lines = [Text.Encoding]::ASCII.GetString($data) -split "`r?`n"
                    if ($lines[0] -notmatch '^HTTP/1\.[01] 200(?: |$)') { continue }
                    $locationLines = @($lines | Where-Object { $_ -match '^(?i)LOCATION\s*:' })
                    if ($locationLines.Count -ne 1) { continue }
                    $value = ($locationLines[0] -replace '^[^:]+:', '').Trim()
                    $uri = Get-SafeEndpoint $value $null
                    $ip = $null
                    if (-not [Net.IPAddress]::TryParse($uri.DnsSafeHost, [ref]$ip) -or -not $ip.Equals($remote.Address)) { continue }
                    if ($locations.Count -lt 32 -and $seen.Add($uri.AbsoluteUri)) { $locations.Add($uri) }
                } catch { continue }
            }
            Start-Sleep -Milliseconds 25
        }
    } finally { foreach ($socket in $sockets) { $socket.Close() } }
    $script:Report.Discovery.CandidateLocations = $locations.Count
    return $locations.ToArray()
}

$exitCode = 3
$reportFolder = $null
$zipPath = $null
try {
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'DlnaDiagnostics' }
    $parent = [IO.Path]::GetFullPath($OutputDirectory)
    [void][IO.Directory]::CreateDirectory($parent)
    $runName = 'DlnaDiagnostics-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $reportFolder = Join-Path $parent $runName
    [void][IO.Directory]::CreateDirectory($reportFolder)
    $zipPath = Join-Path $parent ($runName + '.zip')
    Write-Host 'AMG DIGA Archive: read-only DLNA diagnostics'
    Write-Host 'No recordings are downloaded. Response bodies and private identifiers are not saved.'
    if ($script:CompareSerialization) { Write-Host 'Browse comparison: enabled after a failed first Browse (at most two requests).' }
    else { Write-Host 'Browse comparison: disabled by AppEquivalentOnly (one request).' }
    $device = $null
    if ($DescriptionUrl) {
        $uri = Get-SafeEndpoint $DescriptionUrl $null
        $exitCode = 2
        $device = Get-Description $uri
    } else {
        Write-Host 'Discovering MediaServer/ContentDirectory devices on active IPv4 multicast interfaces...'
        $locations = @(Find-DescriptionLocations)
        $devices = New-Object 'Collections.Generic.List[object]'
        $discoveryWatch = [Diagnostics.Stopwatch]::StartNew()
        foreach ($location in $locations) {
            if ($discoveryWatch.Elapsed.TotalSeconds -ge 60) { $script:Report.Discovery.BudgetReached = $true; break }
            $script:Report.Discovery.DescriptionsAttempted++
            try { $devices.Add((Get-Description $location)) } catch { continue }
        }
        if ($devices.Count -eq 1) { $device = $devices[0] }
        elseif ($devices.Count -gt 1 -and -not $NonInteractive) {
            Write-Host 'Select the recorder. Names below are displayed locally and omitted from the report.'
            for ($i = 0; $i -lt $devices.Count; $i++) {
                $name = $devices[$i].FriendlyName -replace '[\x00-\x1f\x7f]', ''
                if ($name.Length -gt 100) { $name = $name.Substring(0, 100) }
                Write-Host ('{0}. {1}' -f ($i + 1), $name)
            }
            $selected = 0
            $answer = Read-Host 'Recorder number (or Enter to cancel)'
            if (-not [int]::TryParse($answer, [ref]$selected) -or $selected -lt 1 -or $selected -gt $devices.Count) { throw 'SelectionRequired' }
            $device = $devices[$selected - 1]
        } elseif (-not $NonInteractive) {
            Write-Host 'No usable device found. Use the device-description URL, not a recording URL.'
            $answer = Read-Host 'Description URL, for example http://192.168.1.10:port/path.xml (or Enter to cancel)'
            if (-not $answer) { throw 'DescriptionUrlRequired' }
            $uri = Get-SafeEndpoint $answer $null
            $exitCode = 2
            $device = Get-Description $uri
        } else { throw 'DescriptionUrlRequiredOrMultipleDevices' }
    }
    $exitCode = 2
    $script:Report.Device.ContentDirectoryFound = $true
    $script:Report.Device.ServiceVersion = $device.Version
    $script:Report.Device.UrlBasePresent = $device.UrlBasePresent
    $script:Report.Device.SameHostControl = $true
    $script:Report.Device.ControlPortMatchesDescription = ($device.ControlUri.Scheme -eq $device.DescriptionUri.Scheme -and $device.ControlUri.Port -eq $device.DescriptionUri.Port)
    Write-Host 'Sending one app-equivalent canonical u:Browse (at most 100 objects; no folder recursion)...'
    $succeeded = Invoke-RootBrowse $device $true
    if (-not $succeeded -and $script:CompareSerialization) {
        Write-Host 'First Browse failed. Trying one legacy default-namespace comparison (previous app request).'
        $succeeded = Invoke-RootBrowse $device $false
        if ($succeeded) {
            $script:Report.Outcome = 'LegacyComparisonSucceeded'
            Write-Host 'Legacy comparison succeeded; the current app-equivalent Browse failed.'
        }
    } elseif ($succeeded) {
        $script:Report.Outcome = 'AppEquivalentBrowseSucceeded'
        Write-Host 'App-equivalent Browse succeeded.'
    }
    if ($succeeded) { $exitCode = 0 } else { $script:Report.Outcome = 'BrowseFailed' }
} catch {
    $category = $_.Exception.Message
    $allowedErrors = @('InvalidEndpoint', 'CrossHostEndpointRejected', 'UseRecorderIpAddress', 'NonLocalEndpointRejected', 'DescriptionHttpFailed', 'DescriptionXmlInvalid', 'DescriptionRootInvalid', 'ControlUrlMissing', 'DeviceElementMissing', 'ContentDirectoryMissing', 'SelectionRequired', 'DescriptionUrlRequired', 'DescriptionUrlRequiredOrMultipleDevices')
    if ($category -notin $allowedErrors) { $category = 'DiagnosticSetupOrProcessingFailed' }
    $script:Report.ErrorCategory = $category
    $script:Report.Outcome = 'DiagnosticFailed'
    Write-Host ('Diagnostic stopped: ' + $category)
    if ($category -eq 'UseRecorderIpAddress') { Write-Host 'Use the recorder LAN IP address in the URL. Hostname DNS resolution is intentionally disabled.' }
} finally {
    $script:Report.ExitCode = $exitCode
    if ($reportFolder) {
        try {
            $json = $script:Report | ConvertTo-Json -Depth 16
            [IO.File]::WriteAllText((Join-Path $reportFolder 'report.json'), $json, (New-Object Text.UTF8Encoding($false)))
            $readme = @'
AMG DIGA Archive DLNA diagnostic report (sanitized)

This ZIP was created locally. Nothing was uploaded.
report.json contains HTTP status, standard status name, safe header categories,
byte counts, XML structure counts and separate SOAP/DIDL parse outcomes.
It omits request URLs, raw bodies, recording titles, object IDs, UDNs, serial
numbers, cookies, tokens, custom server reason phrases and error descriptions.
Unknown XML names and header values are replaced with [other].
ScriptSha256 identifies the script actually executed. Build contains only
validated version, commit and modified-source fields from local BUILDINFO.json,
when present; absent values remain null. This metadata is not a signature.

Only device-description GETs and at most two root ContentDirectory Browse
requests were made. No recordings were downloaded, no folder was recursively
browsed and no recorder or Windows settings were changed.

AppEquivalent uses the app's current canonical u:Browse SOAP body and explicit header shape;
the HTTP framework differs (HttpWebRequest here, HttpClient in the app).
LegacyDefaultNamespace uses the previous app's default-namespace serialization
with unqualified arguments. By default this comparison runs once after a failed
AppEquivalent Browse, and never after a
successful first Browse. AppEquivalentOnly disables it, including when the
legacy TryCanonicalBrowse switch is also supplied. MaximumBrowseRequests is 2
by default and 1 with AppEquivalentOnly. AppEquivalentBrowseSucceeded means the
current app-equivalent request passed. LegacyComparisonSucceeded means only the
previous serialization passed, while the current app-equivalent request failed.
SchemaVersion 2: AppEquivalent is the u:Browse request. In SchemaVersion 1
reports AppEquivalent was the default-namespace request and CanonicalPrefix was
the u:Browse request. Always read Request.BrowseNamespace together with Variant.
Request.ConnectionHeader records the Connection header .NET Framework added to
that request (absent or keep-alive); the application sends none on Browse.
ResponseHttpVersion and Device.ControlPortMatchesDescription describe the
usual conditions under which .NET Framework adds it; it can also appear after
a request that received no response. The recorded value is authoritative.
When a request received no HTTP response (StatusCode is null),
ResponseHttpVersion is null and Request.ConnectionHeader is keep-alive only if
that header had been added to the request; otherwise it is null, meaning the
request went out without the header or was never sent.
A success is not proof of a universal Panasonic fix. Missing/empty Result is distinguished
from an empty HTTP body and malformed nested DIDL XML.

ReceivedBytes counts response entity bytes delivered by .NET after HTTP transfer
framing, without content decompression. A rejected/oversized response may not be
read in full. Xml/DIDL error line and position contain no source text.
ReasonPhrase is the standard HTTP status name; untrusted server text is omitted.

Exit 0: a root Browse passed XML, nested DIDL and object-count checks.
Exit 2: the diagnostic ran, but device description or root Browse failed.
Exit 3: input, selection or local setup failed.
Discovery is bounded; discovery failure does not prove the recorder is offline.
This tool does not establish whether any recording can be downloaded or played.
'@
            [IO.File]::WriteAllText((Join-Path $reportFolder 'README.txt'), $readme, (New-Object Text.UTF8Encoding($false)))
            [IO.Compression.ZipFile]::CreateFromDirectory($reportFolder, $zipPath)
            Write-Host ('Report folder: ' + $reportFolder)
            Write-Host ('Share this sanitized ZIP: ' + $zipPath)
        } catch { $exitCode = 3; Write-Host 'Could not finish saving the report ZIP. Check the output directory and available space.' }
    }
    Write-Host ('Finished. Exit code: ' + $exitCode + '. No data was uploaded.')
}
exit $exitCode
