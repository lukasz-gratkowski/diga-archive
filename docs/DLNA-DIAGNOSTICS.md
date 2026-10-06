# Diagnose a recorder on another network

## Po polsku — szybki start

Uruchom pakiet na komputerze z systemem Windows podłączonym do **tej samej sieci domowej co nagrywarka**. Wystarczy Windows PowerShell 5.1; nie trzeba instalować aplikacji ani dodatkowych narzędzi. Komunikaty skryptu są po angielsku.

1. Rozpakuj cały pakiet do nowego folderu.
2. Włącz nagrywarkę i jej serwer DLNA, a następnie wyjdź z menu ustawień nagrywarki.
3. Kliknij dwukrotnie **Run-DlnaDiagnostics.cmd**. Wiersz zaczynający się od `Browse comparison: enabled` potwierdza, że porównanie formatów zapytania jest włączone. Jeśli skrypt znajdzie kilka urządzeń, wpisz numer nagrywarki i naciśnij Enter. Jeśli nie znajdzie żadnego, poprosi o adres opisu urządzenia (`Description URL`) — to adres pliku XML rozgłaszany przez nagrywarkę, a nie adres nagrania; gdy go nie znasz, nie zgaduj, tylko naciśnij sam Enter.
4. Odeślij do analizy plik `DlnaDiagnostics-….zip`; jego ścieżkę skrypt wypisuje pod koniec, w wierszu `Share this sanitized ZIP:`. Zrób to także wtedy, gdy diagnostyka zakończy się błędem. Skrypt nie pobiera nagrań, nie zmienia ustawień nagrywarki i niczego nie wysyła samodzielnie.

Pierwsze zapytanie (`AppEquivalent`) ma format `u:Browse`, którego aplikacja używa od wersji 0.4.3. Drugie (`LegacyDefaultNamespace`, format aplikacji do wersji 0.4.2 włącznie) jest wysyłane tylko wtedy, gdy pierwsze się nie powiedzie; dlatego pole `Limits.MaximumBrowseRequests` w raporcie ma domyślnie wartość 2. Jeśli pierwsze zapytanie się powiedzie, raport zawiera jedno zapytanie Browse z HTTP 200 oraz `Outcome: AppEquivalentBrowseSucceeded`; brak drugiego wariantu jest wtedy prawidłowy.

W raporcie z 1 października 2026 r., utworzonym wcześniejszą wersją skryptu, nagrywarka odpowiedziała na starszy format kodem HTTP 412, a na zapytanie `u:Browse` — kodem HTTP 200 i jednym folderem głównym. Na tej podstawie w wersji 0.4.3 zmieniono format zapytania wysyłanego przez aplikację. Pakiet odczytuje wyłącznie katalog główny: nie sprawdza zawartości folderów ani możliwości pobrania nagrań. W chwili wydania wersji 0.4.3 sama aplikacja nie była jeszcze sprawdzona z tą nagrywarką.

## Run the diagnostic

Run this kit on a Windows PC connected to the **same home network as the recorder**. Windows PowerShell 5.1 is sufficient; no application installation, SDK, administrator rights or additional tools are required.

1. Extract the diagnostic ZIP completely. Keep `Run-DlnaDiagnostics.cmd` beside `Test-DlnaRecorder.ps1`.
2. Turn on the recorder and its home-network/DLNA server, then exit its settings menus. Double-click **Run-DlnaDiagnostics.cmd**. The console confirms that Browse comparison is enabled.
3. Select the recorder if several are discovered. If discovery is blocked, the script offers a device-description URL prompt. This is the XML description URL advertised by the recorder, not a video URL; do not guess its port/path.
4. Wait for the final **Share this sanitized ZIP** path. Send that ZIP back for analysis. The original response bodies are never saved. Nothing is uploaded automatically.

The output normally appears in a new `DlnaDiagnostics` folder beside the scripts. Each run creates its own timestamped folder and ZIP; existing reports are not overwritten. If that location is not writable, use `-OutputDirectory` to choose a writable folder.

## Repeat a known device-description URL

Open Windows PowerShell in the extracted scripts folder. Replace the example URL with the recorder's actual device-description URL:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test-DlnaRecorder.ps1 -DescriptionUrl 'http://192.168.1.10:1234/description.xml' -OutputDirectory "$env:USERPROFILE\Desktop\DlnaDiagnostics" -NonInteractive
```

The URL must use a private, link-local or loopback IP address. `localhost` is also supported for local tests; other DNS names are intentionally rejected to prevent DNS rebinding. HTTP and HTTPS are accepted, with normal certificate validation. A self-signed or name-mismatched HTTPS certificate will fail rather than bypass validation. Credentials, fragments, public addresses and changes of host through `URLBase` or `controlURL` are rejected. Same-host service ports may differ. Redirects, proxies, cookies and automatic Windows authentication are disabled.

`-NonInteractive` never prompts. Without `-DescriptionUrl`, it can select a single discovered usable device; zero or multiple devices require rerunning with an explicit URL. `-DiscoverySeconds` accepts 1–10 seconds (default 3); `-TimeoutSeconds` accepts 1–30 seconds per HTTP exchange (default 10).

## What is checked

The script sends bounded SSDP searches on all active IPv4 multicast interfaces. It accepts only local responder addresses with a matching IP-literal `LOCATION`, reads bounded device descriptions and selects the ContentDirectory service. Description collection has a 60-second budget plus the current request's timeout. SSDP responses are capped at 256 and unique locations at 32.

For the selected recorder it first sends **one root Browse**, requesting at most 100 objects, matching the current application's SOAP serialization, argument order and explicit headers. In version 0.4.3 this primary `AppEquivalent` request uses the conventional `u:Browse` prefix with unqualified arguments. The HTTP frameworks differ: the standalone script uses .NET Framework `HttpWebRequest`, while the application uses `HttpClient`. This is a SOAP/header comparison, not a byte-for-byte claim about every transport detail: the SOAP body and the four header lines both programs send (`Host`, `SOAPAction`, `Content-Type`, `Content-Length`) are the same, but the application writes `Host` first while the script writes it after `Content-Type` and `SOAPAction`, and the script can carry a fifth header, `Connection`, described below. It does not recurse through folders or download any recording. **Comparison is enabled by default**, including when the PowerShell script is run directly: only if the first Browse fails, it sends one additional `LegacyDefaultNamespace` root Browse using the pre-0.4.3 default-namespace serialization. The script applies the same request settings to both variants. .NET Framework itself adds `Connection: Keep-Alive` to every request it sends before it has received a first HTTP/1.1 response from that host:port (normally just the first request), and to every request while the server answers HTTP/1.0. After a request that timed out it can add the header again, because it may discard what it had learned about that host:port. The two Browse requests can therefore differ in that header, usually when the control URL uses a different port than the description; `Request.ConnectionHeader`, when recorded, is authoritative, and the other two fields only explain the usual cases. The application sends no `Connection` header on Browse. The report records `Request.ConnectionHeader`, `ResponseHttpVersion` and `Device.ControlPortMatchesDescription`; the 2026-10-01 comparison report predates these fields. When a request received no HTTP response (`StatusCode` is null), `ResponseHttpVersion` is null and `Request.ConnectionHeader` is `keep-alive` only if that header had been added to the request; otherwise it is null, meaning the request went out without the header or was never sent. Use `-AppEquivalentOnly` to disable the comparison explicitly. The old `-TryCanonicalBrowse` option remains accepted for compatibility with earlier launchers and command lines; `-AppEquivalentOnly` takes precedence if both options are supplied. A successful secondary comparison is identified as `LegacyComparisonSucceeded`.

The revised script prints whether comparison is enabled before discovery. Its report has `Limits.MaximumBrowseRequests = 2` by default; a value of `1` means the explicit opt-out was used or an older script was run. The originally published v0.4.2 kit enabled comparison only through its CMD launcher or the old opt-in flag. The separate 2026-10-01 comparison kit made that comparison automatic but still tested the older app format first, followed by `CanonicalPrefix`. Existing kits and v0.4.2 release assets are unchanged. Reports from the 0.4.3 script carry `SchemaVersion: 2`, where `AppEquivalent` is the `u:Browse` request and `LegacyDefaultNamespace` the older one; in `SchemaVersion: 1` reports `AppEquivalent` was the default-namespace request and `CanonicalPrefix` the `u:Browse` request. Always read `Request.BrowseNamespace` together with `Variant`, and check `ScriptSha256` (and `Build.Version`, which is null when the script is run without `BUILDINFO.json`) when comparing reports across these revisions.

The report identifies both variants explicitly. A successful comparison is evidence to investigate, not a promise that the variant is a universal Panasonic fix. Device settings, firewall rules and files on the recorder are never changed; ports are never scanned.

HTTP responses are limited to approximately 4 MiB and are not decompressed. XML external entities and DTDs are disabled; nesting is limited to 64 levels. The report records:

- HTTP status and its standard name, safe header categories, declared length and bytes received.
- Empty, incomplete, oversized, encoded or non-XML response categories.
- Outer XML root/known element counts and parse status.
- A separate nested DIDL parse status, including declaration-only malformed results.
- Numeric UPnP error codes, Browse counts and whether returned objects match those counts.

An empty HTTP error body keeps its HTTP status; it is not replaced by the XML parser's “Root element is missing” message. An empty `<Result/>` is separately recognized as zero objects. Status **Success** validates only this first page's XML and counts; it does not confirm that every recording is available, downloadable, unprotected or playable.

## Privacy and exit codes

`report.json` and its explanatory `README.txt` are the only ZIP contents. They omit raw response content, device names/addresses/URLs, UDNs, serial numbers, recording titles, object identifiers, authentication data and response error descriptions. Unknown XML names and header values are replaced by categories. Device names may be shown locally when selecting between discovered devices, but are not retained. No separate private endpoint file is created.

The report includes the SHA-256 hash of the executed script. If `BUILDINFO.json` is beside the script or one folder above it, only validated version, commit hash and modified-source status are copied. This helps identify the diagnostic version without copying arbitrary file content; it is provenance information, not a digital signature.

Exit **0** means a root Browse passed the structural checks; it does not say which variant passed. Check `Outcome` in `report.json`: `AppEquivalentBrowseSucceeded` means the current application format passed, while `LegacyComparisonSucceeded` also exits 0 but means the current application format failed and only the pre-0.4.3 default-namespace format passed. Exit **2** means the diagnostic captured a failed description/Browse. Exit **3** means input, selection or local setup failed. A failed diagnostic is still useful: share the ZIP when one was produced. Discovery failure alone does not establish that a recorder is offline.

The launcher uses a process-only PowerShell execution-policy option; it does not persistently change Windows policy. Managed computers may still prohibit script execution. Use the organization's approved method in that case.

## Basis for the comparison

The [UPnP architecture specification](https://upnp.org/specs/arch/UPnP-arch-DeviceArchitecture-v1.1-20081015.pdf) defines SOAP action framing and makes User-Agent optional. Its extended-type negotiation is also a reason not to add an arbitrary UPnP/1.1 User-Agent during diagnosis. [VLC's implementation](https://raw.githubusercontent.com/videolan/vlc/master/modules/services_discovery/upnp.cpp) uses the same root Browse arguments, while [libupnp's SOAP transport](https://raw.githubusercontent.com/pupnp/pupnp/master/upnp/src/soap/soap_ctrlpt.c) demonstrates a conventional envelope. These sources do not prove that a particular Panasonic recorder requires a special header or prefix. One hardware observation now exists. On 2026-10-01 a recorder reported by its owner as a DMR-BS850 (ContentDirectory:2) answered the default-namespace root Browse with an empty HTTP 412 and the `u:Browse` root Browse, sent immediately afterwards in the same run, with HTTP 200 and one folder. The two request bodies differ in the action prefix and in the `xmlns=""` declaration that the default-namespace form places on each of the six arguments, so the report does not show which of the two the recorder rejects. The `u:Browse` form has not yet been observed as the first request, which is how version 0.4.3 sends it. One run on one device is not a general Panasonic rule.

Some Panasonic models also require registering the PC in their DLNA server settings. Check the manual for the actual model; the friendly name “DIGA BD/DVD Recorder” does not identify it. For example, [the official BS850 manual, page 97](https://tda.panasonic-europe-service.com/docs/1524838079-6011-FAEB2CDE788885D0490B6F15271A8D97AF771E13/tsn2/data/ALL/DMRBS850/OI/836579/rqt9434-l.pdf) describes registration of non-Panasonic equipment. The script does not change those settings.
