#!/usr/bin/env python3
"""Sequential, bounded listing/EPG diagnostics. Never opens a channel stream.

Input: private JSON array of {label, url, kind: playlist|epg|portal}.
Reports omit URLs, bodies, exception messages and provider credentials.
"""
import argparse
import datetime
import gzip
import hashlib
import io
import json
import lzma
import os
from pathlib import Path
import re
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET


def inspect_body(body, kind):
    result = {"sha256": hashlib.sha256(body).hexdigest()}
    if kind == "epg":
        stream = io.BytesIO(body)
        if body.startswith(b"\x1f\x8b"):
            stream = gzip.GzipFile(fileobj=stream)
        elif body.startswith(b"\xfd7zXZ"):
            stream = lzma.LZMAFile(stream)
        # Bound expanded data too, including compressed inputs.
        expanded = stream.read(256 * 1024 * 1024 + 1)
        if len(expanded) > 256 * 1024 * 1024:
            return dict(result, valid=False, reason="ExpandedSizeLimit")
        if b"<!DOCTYPE" in expanded.upper() or b"<!ENTITY" in expanded.upper():
            return dict(result, valid=False, reason="XmlEntitiesRejected")
        counts = {"channel": 0, "programme": 0}
        first = last = None
        root_tag = None
        for event, element in ET.iterparse(io.BytesIO(expanded), events=("start", "end")):
            if root_tag is None:
                root_tag = element.tag
            if event == "end":
                if element.tag in counts:
                    counts[element.tag] += 1
                if element.tag == "programme":
                    start = element.get("start", "")
                    stop = element.get("stop", "")
                    first = min(first or start, start)
                    last = max(last or stop, stop)
                element.clear()
        return dict(result, valid=root_tag == "tv" and counts["programme"] > 0,
                    expandedBytes=len(expanded), channels=counts["channel"],
                    programmes=counts["programme"], firstProgramme=first, lastProgramme=last)
    text = body.decode("utf-8-sig", errors="replace")
    if kind == "playlist":
        lines = text.splitlines()
        urls = [line.strip() for line in lines if line.strip().startswith(("http://", "https://"))]
        origins = sorted({urllib.parse.urlsplit(url).hostname for url in urls})
        entries = sum(line.startswith("#EXTINF:") for line in lines)
        # Digests allow comparison without exposing access tokens or stream paths.
        stream_hash = hashlib.sha256("\n".join(sorted(urls)).encode()).hexdigest()
        return dict(result, valid=text.lstrip().startswith("#EXTM3U") and entries > 0,
                    format="m3u" if text.lstrip().startswith("#EXTM3U") else "other",
                    entries=entries, streamUrls=len(urls), origins=origins,
                    streamSetSha256=stream_hash, groups=len(set(re.findall(r'group-title="([^"]*)"', text))))
    return dict(result, format="json" if text.lstrip().startswith(("{", "[")) else
                "xml/html" if text.lstrip().startswith("<") else "text")


def probe(entry, cache, timeout, max_bytes):
    start = time.monotonic()
    result = {"label": entry["label"], "kind": entry["kind"],
              "requestedScheme": urllib.parse.urlsplit(entry["url"]).scheme}
    try:
        request = urllib.request.Request(entry["url"], headers={"User-Agent": "Jellyfin IPTV diagnostics", "Accept-Encoding": "identity"})
        with urllib.request.urlopen(request, timeout=min(timeout, 8)) as response:
            result.update(status=response.status, finalHost=urllib.parse.urlsplit(response.url).hostname,
                          finalScheme=urllib.parse.urlsplit(response.url).scheme,
                          headersMs=round((time.monotonic() - start) * 1000),
                          contentType=response.headers.get_content_type())
            chunks = []
            size = 0
            while True:
                chunk = response.read1(65536)
                if not chunk:
                    break
                if not chunks:
                    result["firstByteMs"] = round((time.monotonic() - start) * 1000)
                chunks.append(chunk)
                size += len(chunk)
                if size > max_bytes:
                    result["reason"] = "BodySizeLimit"
                    raise ValueError()
                if time.monotonic() - start > timeout:
                    raise TimeoutError()
            result.update(bytes=size, transferMs=round((time.monotonic() - start) * 1000))
            body = b"".join(chunks)
            result.update(inspect_body(body, entry["kind"]))
            if cache is not None:
                name = hashlib.sha256(entry["label"].encode()).hexdigest()[:16] + ".body"
                path = cache / name
                path.write_bytes(body)
                path.chmod(0o600)
                result["cacheFile"] = name
    except urllib.error.HTTPError as error:
        result.update(status=error.code, valid=False, reason="HttpError")
    except Exception as error:
        # Never print exception strings: urllib/decoder errors can include the URL.
        result.update(valid=False, reason=result.get("reason", type(error).__name__))
    result["totalMs"] = round((time.monotonic() - start) * 1000)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("catalog", type=Path)
    parser.add_argument("report", type=Path)
    parser.add_argument("--private-cache", type=Path)
    parser.add_argument("--rounds", type=int, default=1)
    parser.add_argument("--timeout", type=float, default=45)
    parser.add_argument("--max-mib", type=int, default=64)
    args = parser.parse_args()
    os.umask(0o077)
    if args.private_cache:
        args.private_cache.mkdir(parents=True, exist_ok=True, mode=0o700)
    entries = json.loads(args.catalog.read_text(encoding="utf-8-sig"))
    report = {"startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "scope": "Listing and EPG downloads only; not media availability", "results": []}
    for turn in range(args.rounds):
        for entry in entries:
            result = probe(entry, args.private_cache, args.timeout, args.max_mib * 1024 * 1024)
            result["round"] = turn + 1
            report["results"].append(result)
            args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            print(json.dumps(result), flush=True)
            if result.get("status") in (401, 403, 429, 453, 509):
                print("Account/capacity response: stopping this audit.", flush=True)
                return 2
            time.sleep(0.3)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
