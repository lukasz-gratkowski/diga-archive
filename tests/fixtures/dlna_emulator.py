"""Loopback-only manual UI fixture. Usage: python dlna_emulator.py --media generated.mpg

Serve only generated test media; this is a DLNA HTTP/SOAP emulator, not an SSDP server.
Versions up to 0.5.0 could open the printed description URL in the app's manual connection panel.
That panel was removed in 0.5.1 and this emulator does not answer SSDP, so the current app cannot
find it; it remains usable with the diagnostic script's -DescriptionUrl option. Ctrl+C stops it.
"""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--media", type=Path, required=True)
parser.add_argument("--port", type=int, default=0)
args = parser.parse_args()
media = args.media.resolve(strict=True)
if not media.is_file():
    parser.error("--media must name a generated test video file")

DIDL = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"
DC = "http://purl.org/dc/elements/1.1/"
SOAP = "http://schemas.xmlsoap.org/soap/envelope/"
SERVICE = "urn:schemas-upnp-org:service:ContentDirectory:1"


class Recorder(BaseHTTPRequestHandler):
    def send(self, body, mime="text/xml; charset=utf-8", features=None):
        self.send_response(200)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(body)))
        if features:
            self.send_header("contentFeatures.dlna.org", features)
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/description.xml":
            self.send(b'''<root xmlns="urn:schemas-upnp-org:device-1-0"><device>
              <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
              <friendlyName>AMG DIGA demo recorder</friendlyName><manufacturer>Test fixture</manufacturer>
              <modelName>Loopback emulator</modelName><UDN>uuid:diga-ui-demo</UDN><serviceList><service>
              <serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>
              <controlURL>/control</controlURL></service></serviceList></device></root>''')
        elif self.path in ("/recording.mpg", "/unknown.mpg"):
            self.send(media.read_bytes(), "video/mpeg", "DLNA.ORG_CI=0" if self.path == "/recording.mpg" else None)
        else:
            self.send_error(404)

    def do_POST(self):
        if self.path != "/control":
            self.send_error(404)
            return
        length = int(self.headers.get("Content-Length", "0"))
        if not 0 < length <= 16384:
            self.send_error(400)
            return
        request = ET.fromstring(self.rfile.read(length))
        values = {node.tag.rsplit("}", 1)[-1]: node.text for node in request.iter()}
        didl = ET.Element(f"{{{DIDL}}}DIDL-Lite")
        if values.get("ObjectID") == "0":
            folder = ET.SubElement(didl, f"{{{DIDL}}}container", id="recordings", parentID="0", restricted="1")
            ET.SubElement(folder, f"{{{DC}}}title").text = "Demo recordings"
        else:
            for identifier, title, flags, path in [
                ("original", "Family & travel 録画", "DLNA.ORG_CI=0", "/recording.mpg"),
                ("unknown", "Unknown conversion status", "*", "/unknown.mpg"),
                ("protected", "Protected programme", "DTCP1;DLNA.ORG_CI=0", "/protected"),
                ("converted", "Converted resource", "DLNA.ORG_CI=1", "/converted"),
            ]:
                item = ET.SubElement(didl, f"{{{DIDL}}}item", id=identifier, parentID="recordings", restricted="1")
                ET.SubElement(item, f"{{{DC}}}title").text = title
                res = ET.SubElement(item, f"{{{DIDL}}}res", protocolInfo=f"http-get:*:video/mpeg:{flags}")
                if identifier != "unknown":
                    res.set("size", str(media.stat().st_size))
                res.text = f"http://127.0.0.1:{self.server.server_port}{path}"
        envelope = ET.Element(f"{{{SOAP}}}Envelope")
        response = ET.SubElement(ET.SubElement(envelope, f"{{{SOAP}}}Body"), f"{{{SERVICE}}}BrowseResponse")
        ET.SubElement(response, "Result").text = ET.tostring(didl, encoding="unicode")
        ET.SubElement(response, "NumberReturned").text = str(len(didl))
        ET.SubElement(response, "TotalMatches").text = str(len(didl))
        ET.SubElement(response, "UpdateID").text = "1"
        self.send(ET.tostring(envelope, encoding="utf-8"))


server = ThreadingHTTPServer(("127.0.0.1", args.port), Recorder)
print(f"Description: http://127.0.0.1:{server.server_port}/description.xml", flush=True)
print(f"Direct URL:  http://127.0.0.1:{server.server_port}/recording.mpg", flush=True)
try:
    server.serve_forever()
except KeyboardInterrupt:
    pass
finally:
    server.server_close()
