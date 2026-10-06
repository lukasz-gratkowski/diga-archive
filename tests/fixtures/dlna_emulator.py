"""A stand-in network recorder for trying the application without a recorder.

Usage: python dlna_emulator.py --media generated.mpg [--announce]

Serves only the given test video. Without --announce it listens on 127.0.0.1 and is reachable by the
diagnostic script's -DescriptionUrl option. With --announce it listens on this computer's LAN address and
answers SSDP searches, so "Find network recorders" in the application lists it. The catalogue holds one
folder with a downloadable recording, one of unknown conversion status, one advertised as protected and one
advertised as converted. Ctrl+C stops it.
"""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import socket
import struct
import threading
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--media", type=Path, required=True)
parser.add_argument("--port", type=int, default=0)
parser.add_argument("--announce", action="store_true", help="listen on the LAN address and answer SSDP searches")
parser.add_argument("--address", help="the LAN address to use with --announce; found automatically when omitted")
args = parser.parse_args()
media = args.media.resolve(strict=True)
if not media.is_file():
    parser.error("--media must name a generated test video file")

DIDL = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/"
DC = "http://purl.org/dc/elements/1.1/"
SOAP = "http://schemas.xmlsoap.org/soap/envelope/"
SERVICE = "urn:schemas-upnp-org:service:ContentDirectory:1"
DEVICE = "urn:schemas-upnp-org:device:MediaServer:1"
GROUP = "239.255.255.250"


def lan_address():
    # No packet is sent: connecting a UDP socket only makes the system choose the interface of the default route.
    # 192.0.2.1 is a documentation address (RFC 5737).
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.connect(("192.0.2.1", 9))
        return probe.getsockname()[0]


host = (args.address or lan_address()) if args.announce else "127.0.0.1"


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
              <friendlyName>DIGA demo recorder</friendlyName><manufacturer>Test fixture</manufacturer>
              <modelName>Emulator</modelName><UDN>uuid:diga-ui-demo</UDN><serviceList><service>
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
                res.text = f"http://{host}:{self.server.server_port}{path}"
        envelope = ET.Element(f"{{{SOAP}}}Envelope")
        response = ET.SubElement(ET.SubElement(envelope, f"{{{SOAP}}}Body"), f"{{{SERVICE}}}BrowseResponse")
        ET.SubElement(response, "Result").text = ET.tostring(didl, encoding="unicode")
        ET.SubElement(response, "NumberReturned").text = str(len(didl))
        ET.SubElement(response, "TotalMatches").text = str(len(didl))
        ET.SubElement(response, "UpdateID").text = "1"
        self.send(ET.tostring(envelope, encoding="utf-8"))


def answer_searches(location):
    """Replies to M-SEARCH for a media server or a content directory, from the address the location names."""
    listener = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("", 1900))
    listener.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP, struct.pack("4s4s", socket.inet_aton(GROUP), socket.inet_aton(host)))
    reply = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    reply.bind((host, 0))
    while True:
        data, sender = listener.recvfrom(4096)
        text = data.decode("ascii", "replace")
        if not text.startswith("M-SEARCH"):
            continue
        target = next((line.split(":", 1)[1].strip() for line in text.split("\r\n") if line.upper().startswith("ST:")), "")
        if target not in (DEVICE, SERVICE, "ssdp:all", "upnp:rootdevice"):
            continue
        answer = ("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=60\r\nEXT:\r\n"
                  f"LOCATION: {location}\r\nSERVER: Test/1.0 UPnP/1.0 DigaEmulator/1.0\r\n"
                  f"ST: {target}\r\nUSN: uuid:diga-ui-demo::{target}\r\n\r\n")
        reply.sendto(answer.encode("ascii"), sender)


server = ThreadingHTTPServer((host, args.port), Recorder)
description = f"http://{host}:{server.server_port}/description.xml"
print(f"Description: {description}", flush=True)
print(f"Direct URL:  http://{host}:{server.server_port}/recording.mpg", flush=True)
if args.announce:
    threading.Thread(target=answer_searches, args=(description,), daemon=True).start()
    print("Answering SSDP searches on 239.255.255.250:1900", flush=True)
try:
    server.serve_forever()
except KeyboardInterrupt:
    pass
finally:
    server.server_close()
