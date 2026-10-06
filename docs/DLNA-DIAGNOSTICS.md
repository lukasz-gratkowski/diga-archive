# Diagnostics kit

*Po polsku: krótka instrukcja jest niżej, w części [Po polsku: szybki start](#po-polsku).*

The diagnostics kit is a small script for Windows. It asks a recorder the same first questions as AMG DIGA Archive and writes down how the recorder answered, without writing down anything private. Use it when the application does not find your recorder, when it finds the recorder but cannot open it, or when you want to report how a recorder model behaves.

This document is also the `README.md` inside the kit. That is why its links are full web addresses.

**Where the kit is.**

- Every release on the [releases page](https://github.com/lukasz-gratkowski/AmgDigaArchive/releases) has a file named `DIGA-…-dlna-diagnostics.zip`. It holds `Run-DlnaDiagnostics.cmd`, `Test-DlnaRecorder.ps1`, this document as `README.md`, `LICENSE`, `BUILDINFO.json` and `SHA256SUMS.txt`.
- The application carries the same two scripts, and this document as `README.md`, in the folder `diagnostics` beside `Diga.exe`. For an installed copy this is `%LOCALAPPDATA%\Programs\DIGA\diagnostics`, unless another folder was chosen.

**What it needs.** A Windows PC connected to the same home network as the recorder. Windows PowerShell 5.1, which is part of Windows, is enough. Nothing is installed, and no administrator rights are needed.

## Run it

1. If you downloaded the ZIP, extract all of it into a new folder. `Run-DlnaDiagnostics.cmd` and `Test-DlnaRecorder.ps1` must stay side by side.
2. Switch on the recorder and its network server (DLNA), then leave the recorder's settings menus. [Recorder setup and troubleshooting](https://github.com/lukasz-gratkowski/AmgDigaArchive/blob/main/docs/RECORDER-SETUP.md) says where the setting is.
3. Double-click `Run-DlnaDiagnostics.cmd`. A text window opens. The line that begins with `Browse comparison: enabled` confirms that the comparison of two request forms is on.
4. Answer the script if it asks:
   - Several devices found: it lists their names. Type the number of the recorder and press Enter.
   - Exactly one device found: it uses that device without asking and without showing its name.
   - No device found: it asks for a `Description URL`. This is the address of the XML description that the recorder announces, not the address of a recording. Do not guess it; press Enter alone.
5. Wait for the line that begins with `Share this sanitized ZIP:`. It gives the path of the report. Press a key to close the window.

The report normally appears in a new folder `DlnaDiagnostics` beside the scripts. Each run makes its own folder and ZIP, named with the date and time in UTC; earlier reports are not overwritten.

## Send the report

Open the [issue chooser](https://github.com/lukasz-gratkowski/AmgDigaArchive/issues/new/choose), pick "Recorder report", and drag the ZIP into the field "Diagnostics". Attach the ZIP as it is. Say which recorder model you have and what the application showed.

Send the report also when the script ended with an error or found no device. A failed run still shows how far the recorder got. The script itself uploads nothing.

<a name="po-polsku"></a>
## Po polsku: szybki start

Zestaw diagnostyczny to mały skrypt dla systemu Windows. Zadaje nagrywarce te same pierwsze pytania co aplikacja AMG DIGA Archive i zapisuje, jak nagrywarka odpowiedziała: bez adresów, bez nazw urządzeń i bez tytułów nagrań. Uruchom go na komputerze podłączonym do tej samej sieci domowej co nagrywarka. Wystarczy Windows PowerShell 5.1, który jest częścią systemu Windows. Niczego nie trzeba instalować. Komunikaty skryptu są po angielsku.

1. Plik ZIP pobrany ze strony wydań rozpakuj w całości do nowego folderu. Ten sam skrypt jest też w folderze `diagnostics` obok programu `Diga.exe`.
2. Włącz nagrywarkę i jej serwer sieciowy (DLNA), a potem wyjdź z menu ustawień nagrywarki.
3. Kliknij dwukrotnie plik `Run-DlnaDiagnostics.cmd`. Jeśli skrypt znajdzie kilka urządzeń, wpisz numer nagrywarki i naciśnij Enter. Jeśli znajdzie dokładnie jedno, użyje go bez pytania. Jeśli nie znajdzie żadnego, poprosi o adres opisu urządzenia (`Description URL`). Nie zgaduj go: naciśnij sam Enter.
4. Poczekaj na wiersz zaczynający się od `Share this sanitized ZIP:`. Podaje on ścieżkę pliku `DlnaDiagnostics-….zip` z raportem.
5. Dołącz ten plik ZIP do zgłoszenia „Recorder report” na stronie <https://github.com/lukasz-gratkowski/AmgDigaArchive/issues/new/choose>. Formularz jest po angielsku, ale możesz pisać po polsku. Wyślij raport także wtedy, gdy skrypt zakończył się błędem albo nie znalazł żadnego urządzenia.

Skrypt nie pobiera nagrań, niczego nie zmienia w nagrywarce ani w systemie Windows i niczego nikomu nie wysyła. Odczytuje tylko najwyższy poziom listy nagrywarki, więc nie pokazuje, czy nagrania w folderach da się zapisać. Więcej po polsku: [Konfiguracja nagrywarki i rozwiązywanie problemów](https://github.com/lukasz-gratkowski/AmgDigaArchive/blob/main/docs/RECORDER-SETUP.pl.md).

## What the kit does

The rest of this document is for people who read the report or change the script. It describes `scripts/Test-DlnaRecorder.ps1` and its launcher `scripts/Run-DlnaDiagnostics.cmd`.

1. **Search.** From every active IPv4 address of the PC (adapters that are up and support multicast, without loopback) the script sends two SSDP `M-SEARCH` messages to `239.255.255.250:1900`, one for `urn:schemas-upnp-org:device:MediaServer:1` and one for `urn:schemas-upnp-org:service:ContentDirectory:1`, with a time-to-live of 1. It listens for `-DiscoverySeconds` seconds (default 3, allowed 1 to 10) and reads at most 256 answers.
2. **Answers.** An answer is used only if it comes from a private, link-local or loopback address, its first line is an HTTP/1.0 or HTTP/1.1 status 200, and it has exactly one `LOCATION` line whose host is the IP address the answer came from. At most 32 different locations are kept.
3. **Descriptions.** The script fetches each location with an HTTP `GET`, one after another, within a budget of 60 seconds plus the time limit of the request in progress. A device is usable if its description is valid XML with the root element `root` and offers a ContentDirectory service (versions 1 to 99) with a control address on the same host. `URLBase` and `controlURL` may name another port but not another host.
4. **One request for the list.** To the selected device the script sends one root `Browse` (`ObjectID` 0, `BrowseDirectChildren`, at most 100 objects). This request, called `AppEquivalent` in the report, has the SOAP body the application has sent since version 0.4.3: the action as `u:Browse` with unqualified arguments.
5. **One comparison, only after a failure.** If the first `Browse` fails, the script sends one more, called `LegacyDefaultNamespace`, in the form the application used up to version 0.4.2. `-AppEquivalentOnly` switches this off. The old option `-TryCanonicalBrowse` is still accepted so that earlier command lines keep working; it changes nothing, and `-AppEquivalentOnly` wins if both are given.
6. **Report.** The script writes `report.json` and `README.txt` into a new folder and packs them into a ZIP.

The script does not open folders, does not download recordings and never scans ports. It does not change the recorder, the firewall or any Windows setting. The launcher passes `-ExecutionPolicy Bypass` to its own PowerShell process only; it does not change the policy of the computer. A managed computer may still forbid scripts. Use the method your organisation allows in that case.

Limits and safeguards:

- Every HTTP exchange has `-TimeoutSeconds` seconds (default 10, allowed 1 to 30).
- Answers are read up to about 4 MiB and are not decompressed.
- XML is parsed with DTDs and external entities switched off, and nesting is limited to 64 levels.
- Redirects, proxies, cookies and automatic Windows sign-in are switched off.
- Addresses must be IP addresses in a private, link-local or loopback range; `localhost` is accepted for local tests. Other host names are refused, so that a name cannot be made to point somewhere else. Addresses with a user name, a password or a fragment are refused.
- HTTPS is accepted with the normal certificate check. A self-signed certificate, or one for another name, makes the request fail.

## What the report contains

`report.json` and `README.txt` are the only files in the ZIP. `README.txt` explains the fields.

The report records:

- for the search: how many local addresses were used, how many sockets failed, how many answers arrived, how many locations were usable, how many descriptions were tried, and whether the 60-second budget ran out;
- for every request: stage, method, HTTP status with its standard name, the response's HTTP version, a short list of header categories, the declared and received number of bytes, the time taken, and a transport error category if there was one;
- for every answer body: whether it was empty, incomplete, too large, encoded or not XML; the root element and the counts of known element names; for a `Browse`, the numeric UPnP error code, `NumberReturned`, `TotalMatches`, and whether the number of returned objects matches;
- about the script: its SHA-256 hash and, if `BUILDINFO.json` lies beside the script or one folder above it, the version, the commit and whether the sources were modified. This identifies the script; it is not a digital signature. In the copy that comes with the application, `Build.ModifiedSources` is null, because the application's `BUILDINFO.json` has no such field.

The report does not contain addresses or URLs, device names, UDNs, serial numbers, recording titles, object identifiers, cookies, sign-in data, the text of error descriptions or any answer body. Element names and header values that the script does not know are replaced by `[other]`. Device names are shown in the text window when you choose between several devices; they are not written to the report.

## Reading the result

| `Outcome` | Exit code | Meaning |
|---|---|---|
| `AppEquivalentBrowseSucceeded` | 0 | The request in the application's current form passed the checks. |
| `LegacyComparisonSucceeded` | 0 | The current form failed and only the form used up to version 0.4.2 passed. |
| `BrowseFailed` | 2 | A device was selected, and every `Browse` that was sent failed. |
| `DiagnosticFailed` | 2 or 3 | The script stopped before a `Browse`. `ErrorCategory` says why. The code is 2 when a description address was given and could not be used as a ContentDirectory device, and 3 for wrong input, for no device or for a cancelled selection. |

Exit code 0 does not say which form passed; read `Outcome`. Exit code 3 without any report means that the report folder or the ZIP could not be written. A code other than 0, 2 or 3 comes from PowerShell itself: the script did not run.

`Browse.Status` is `Success` when the answer was a valid SOAP envelope with a `BrowseResponse`, the nested DIDL-Lite was valid, and the counts agree. It says nothing beyond that first page: not that recordings are listed in the folders, not that they are unprotected, and not that a download would work.

Three things are easy to misread:

- `Browse.Status` is `EnvelopeInvalid` whenever the body was not valid XML. That includes an empty error answer and no answer at all. Read `StatusCode`, `BodyCategory` and `TransportError` to see which it was. An empty HTTP error keeps its status code; it is not replaced by an XML parser message. An empty `Result` is recognised separately as zero objects.
- When a request got no HTTP answer, `StatusCode` and `ResponseHttpVersion` are null.
- A search that finds nothing does not prove that the recorder is off. `Discovery.RepliesReceived` equal to 0 means that no answer reached the script. A number above 0 with `CandidateLocations` equal to 0 means that answers arrived and none was usable. `CandidateLocations` above 0 without a selected device means that the descriptions failed; the entries with `Stage` equal to `Description` show how.

## How the kit differs from the application

The kit is a second implementation, written so that it runs on any Windows PC without installing anything. It is not the application's own code.

| | Application | Kit |
|---|---|---|
| First line of a search answer | exactly `HTTP/1.1 200 OK` | HTTP/1.0 or HTTP/1.1 with status 200 |
| `LOCATION` | exactly one such header before the first empty line | exactly one such line anywhere in the answer |
| Sender of an answer | an IPv4 address in the subnet of the adapter that received the answer, or a link-local address (`169.254.x.x`) | private, link-local or loopback addresses only |
| Limits of the search | 16 local addresses, 256 answers, 64 locations | no limit on local addresses, 256 answers, 32 locations |
| `MX` value in the search | 3 | 2 |
| Reading descriptions | 16 at a time, 3 seconds each, at most 8 from one answering address; the whole search ends after 23 seconds | one after another; budget of 60 seconds |
| Description | any root element | root element must be `root` |
| Device that is used | the one you choose in the list, shown with name, model and address | chosen by number from a list of names; a single device is used without being shown |
| What is read | every folder you open, in parts of 100 entries | the first part of the top level only |
| HTTP library | .NET `HttpClient` | .NET Framework `HttpWebRequest` |
| Control address whose path has a segment that ends in a dot, for example `/dms/control./CD.` | sent as written | sent without those dots (`/dms/control/CD`), because .NET Framework removes them |

Three consequences:

- A device that the kit finds can be missing from the application's list, for example when it answers the search with a first line other than `HTTP/1.1 200 OK`, or from an address outside the subnet of the PC's adapter. The report records neither that line nor addresses, so these cases cannot be read from a report.
- A report with a single device does not show that the device was the recorder. If a television or a network disk was the only device that answered, the report describes that device.
- For a recorder whose control address has a path segment that ends in a dot, the kit and the application send `Browse` to different paths, so one can succeed where the other fails. The report holds no addresses, so this case cannot be read from a report either. The difference was seen in a test with a simulated recorder. Whether any real recorder uses such an address is not known.

The `Browse` request itself was compared on the wire, in a test on one PC with a simulated recorder. The request line was the same, except for a control path with a segment that ends in a dot (the last row of the table). The SOAP body and the four header lines both programs send (`Host`, `SOAPAction`, `Content-Type`, `Content-Length`) are the same. The order of the header lines differs: the application writes `Host` first, the script writes it after `Content-Type` and `SOAPAction`. The script can carry a fifth header, `Connection`, which the application does not send on `Browse`. .NET Framework adds `Connection: Keep-Alive` by itself to every request it sends before it has received a first HTTP/1.1 answer from that host and port (normally only the first request), and to every request while the server answers with HTTP/1.0. After a request that timed out it can add the header again. The two `Browse` requests of one run can therefore differ in that header, usually when the control address uses another port than the description. `Request.ConnectionHeader` in the report is what was sent; `ResponseHttpVersion` and `Device.ControlPortMatchesDescription` only explain the usual cases. When a request got no HTTP answer, `Request.ConnectionHeader` is `keep-alive` only if the header had been added; otherwise it is null, which means that the request went out without the header or was never sent.

## Running it again with options

Open Windows PowerShell in the folder with the scripts. Replace the example address with the recorder's own description address:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test-DlnaRecorder.ps1 -DescriptionUrl 'http://192.168.1.10:1234/description.xml' -OutputDirectory "$env:USERPROFILE\Desktop\DlnaDiagnostics" -NonInteractive
```

- `-DescriptionUrl` skips the search and uses this description.
- `-OutputDirectory` chooses the folder for the reports. Use it when the folder beside the scripts cannot be written.
- `-NonInteractive` never asks. Without `-DescriptionUrl` it uses a single device if exactly one is found; with none or several it stops with exit code 3.
- `-DiscoverySeconds`, `-TimeoutSeconds` and `-AppEquivalentOnly` are described above.

## Report versions

Reports of the current script carry `SchemaVersion: 2` and, by default, `Limits.MaximumBrowseRequests` equal to 2. A value of 1 means that `-AppEquivalentOnly` was used or that an older script ran.

- In `SchemaVersion: 2`, `AppEquivalent` is the `u:Browse` request and `LegacyDefaultNamespace` the older one.
- In `SchemaVersion: 1`, `AppEquivalent` was the default-namespace request, which the application sent at that time, and `CanonicalPrefix` was the `u:Browse` request.
- The kit published with version 0.4.2 made the comparison only when started through its launcher or with the option of that time. A separate kit of 1 October 2026 made the comparison by default, but still sent the older form first.
- `Request.ConnectionHeader`, `ResponseHttpVersion` and `Device.ControlPortMatchesDescription` are newer than the reports of 1 October 2026, which do not have them.

When you compare reports across these versions, read `Request.BrowseNamespace` together with `Variant`, and check `ScriptSha256` and `Build.Version`. `Build.Version` is null when the script ran without a `BUILDINFO.json`.

## What has been seen on real hardware

The project owns no recorder. What is known comes from the owner of one recorder, reported as a DMR-BS850. The reports do not name the device, so the model rests on the owner's word.

- **30 September 2026.** A report showed that the description was answered with HTTP 200 and that the root `Browse` was refused with an empty HTTP 403. This report file was not kept; the statement rests on the project's notes.
- **1 October 2026.** After the owner reported registering the PC's MAC address in the recorder, a report showed an empty HTTP 412 for the root `Browse` in the default-namespace form, which the application sent up to version 0.4.2. A second report the same day, from a kit that tried both forms, showed HTTP 412 for that form and, immediately afterwards in the same run, HTTP 200 with one folder for the `u:Browse` form. The service was ContentDirectory version 2. The two request bodies differ in the action prefix and in the `xmlns=""` declaration that the default-namespace form places on each of the six arguments, so the report does not show which of the two the recorder rejects. The reports of that day do not record the `Connection` header either, so it cannot be excluded that the rejected request carried `Connection: Keep-Alive` and the accepted one did not (see [How the kit differs from the application](#how-the-kit-differs-from-the-application)). On the basis of this comparison, version 0.4.3 of the application changed to the `u:Browse` form.
- **2 October 2026.** The owner reported that version 0.5.2 of the application found the recorder, opened its folders and saved recordings. Version 0.5.2 sends only the `u:Browse` form. This is the owner's report about the application. It is not a report of the kit: no report on file shows the `u:Browse` form sent as the first request to this recorder.

The change from HTTP 403 to another answer after the registration fits what Panasonic's operating instructions for this model say about registering equipment of other makers ([RQT9434-L, page 97](https://tda.panasonic-europe-service.com/docs/1524838079-6011-FAEB2CDE788885D0490B6F15271A8D97AF771E13/tsn2/data/ALL/DMRBS850/OI/836579/rqt9434-l.pdf)). It does not prove that the registration was the cause. One recorder is not a rule for all Panasonic recorders. The friendly name "DIGA BD/DVD Recorder" does not identify a model; check the instructions of the actual model. The script does not change any of these settings.

## Background for the two request forms

The [UPnP Device Architecture specification](https://upnp.org/specs/arch/UPnP-arch-DeviceArchitecture-v1.1-20081015.pdf) defines how a SOAP action is framed and makes the `User-Agent` header optional. Its rules for negotiating extended types are also a reason not to add an arbitrary UPnP/1.1 `User-Agent` while diagnosing. [VLC's implementation](https://raw.githubusercontent.com/videolan/vlc/master/modules/services_discovery/upnp.cpp) uses the same arguments for a root `Browse`, and [libupnp's SOAP transport](https://raw.githubusercontent.com/pupnp/pupnp/master/upnp/src/soap/soap_ctrlpt.c) shows a conventional envelope. These sources do not prove that a particular Panasonic recorder needs a particular header or prefix. A comparison that succeeds is something to look into, not a promise that the same form works on another recorder.
