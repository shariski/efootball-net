"""Refresh the offline provider map from the published AWS and Google Cloud feeds."""

import gzip
import ipaddress
import json
from pathlib import Path
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
SOURCES = {
    "Google Cloud": "https://www.gstatic.com/ipranges/cloud.json",
    "AWS": "https://ip-ranges.amazonaws.com/ip-ranges.json",
}


def read_feed(url):
    with urlopen(url, timeout=30) as response:
        contents = response.read(16 * 1024 * 1024 + 1)
    if len(contents) > 16 * 1024 * 1024:
        raise ValueError(f"Unexpectedly large feed at {url}")
    return json.loads(contents)


def main():
    cities = json.loads((ROOT / "tools/provider-cities.json").read_text(encoding="utf-8"))
    google = read_feed(SOURCES["Google Cloud"])
    aws = read_feed(SOURCES["AWS"])
    entries = []

    def add(prefix, region, provider):
        network = ipaddress.ip_network(prefix)
        name = "Global edge" if region.lower() == "global" else cities[provider].get(region, region)
        entries.append((network, f"{provider} · {name}"))

    before = len(entries)
    for entry in google["prefixes"]:
        add(entry.get("ipv4Prefix") or entry["ipv6Prefix"], entry.get("scope", "global"), "Google Cloud")
    if len(entries) == before:
        raise ValueError("The Google Cloud prefix feed is empty")
    before = len(entries)
    for entry in aws["prefixes"] + aws["ipv6_prefixes"]:
        add(entry.get("ip_prefix") or entry["ipv6_prefix"], entry["region"], "AWS")
    if len(entries) == before:
        raise ValueError("The AWS prefix feed is empty")

    entries = sorted(dict.fromkeys(entries), key=lambda item: item[0].prefixlen, reverse=True)
    rows = "".join(f"{network}\t{label}\n" for network, label in entries)
    output = ROOT / "src/GameDesk/Data/providers.tsv.gz"
    output.write_bytes(gzip.compress(rows.encode("utf-8"), mtime=0))
    print(f"{len(entries)} prefixes. Google Cloud {google['creationTime']}, AWS {aws['createDate']}.")


if __name__ == "__main__":
    main()
