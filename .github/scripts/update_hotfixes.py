import json
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from html import unescape
from pathlib import Path
from urllib.error import URLError
from urllib.parse import urljoin, urlsplit
from urllib.request import Request, urlopen

OUTPUT_FILE = Path("hotfixes.json")
SUPPORT_SEARCH = "https://nvidia.custhelp.com/app/answers/list/kw/hotfix/search/1"
GOOGLE_RSS = (
    "https://news.google.com/rss/search?"
    "q=%22GeForce+Hotfix+Display+Driver%22+OR+%22NVIDIA+GeForce+Hotfix%22&hl=en-US&gl=US&ceid=US:en"
)
TECHPOWERUP = "https://www.techpowerup.com/download/nvidia-geforce-graphics-drivers/"
GURU3D_RSS = "https://www.guru3d.com/rss/news.xml"
FORUM = "https://www.nvidia.com/en-us/geforce/forums/game-ready-drivers/13/"
CDN = "https://international.download.nvidia.com"
CDN_HOSTS = {"international.download.nvidia.com", "us.download.nvidia.com", "download.nvidia.com"}
HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36",
    "Accept-Language": "en-US,en;q=0.9",
}
HREF = re.compile(r"""\bhref\s*=\s*(["'])(.*?)\1""", re.IGNORECASE)
SUPPORT_PATH = re.compile(r"(/app/answers/detail/a_id/[0-9]+)(?:/|$)")
HOTFIX_TITLE = re.compile(
    r"\bGeForce(?: Game Ready)? Hotfix(?: (?:Display )?Driver)?(?: version)?\s+([5-9][0-9]{2}\.[0-9]{2})\b",
    re.IGNORECASE,
)
HOTFIX_SUFFIX = re.compile(
    r"\bGeForce(?: Graphics Drivers?| Driver)?\s+([5-9][0-9]{2}\.[0-9]{2})\s*(?:[-–—]\s*)?Hotfix\b",
    re.IGNORECASE,
)


def fetch_text(url):
    with urlopen(Request(url, headers=HEADERS), timeout=12) as response:
        return response.read().decode("utf-8", errors="replace")


def links(html):
    return (unescape(match[2]) for match in HREF.finditer(html))


def installer_type(url, version):
    parsed = urlsplit(url)
    if parsed.scheme != "https" or parsed.netloc not in CDN_HOSTS:
        return None
    match = re.fullmatch(
        rf"/Windows/{re.escape(version)}hf/{re.escape(version)}-"
        r"(desktop-notebook|desktop|notebook)-[a-z0-9-]+\.hf\.exe",
        parsed.path,
        re.IGNORECASE,
    )
    return match[1].lower() if match else None


def check_installer(url, version, package_type):
    try:
        request = Request(url, method="HEAD", headers=HEADERS)
        with urlopen(request, timeout=8) as response:
            return (
                response.status == 200
                and installer_type(response.geturl(), version) == package_type
                and response.headers.get_content_type() in {
                    "application/octet-stream",
                    "application/x-msdownload",
                    "application/x-msdos-program",
                    "application/vnd.microsoft.portable-executable",
                }
            )
    except (URLError, OSError) as exc:
        print(f"Downloadprüfung fehlgeschlagen ({url}): {exc}")
        return False


def verified_downloads(version, official_urls):
    verified = {}
    for url in official_urls:
        package_type = installer_type(url, version)
        if package_type and package_type not in verified and check_installer(url, version, package_type):
            verified[package_type] = url
    if not verified:
        # NVIDIA Support kann vom Runner aus vorübergehend nicht erreichbar sein.
        for package_type in ("desktop-notebook", "desktop", "notebook"):
            url = f"{CDN}/Windows/{version}hf/{version}-{package_type}-win10-win11-64bit-international-dch.hf.exe"
            if check_installer(url, version, package_type):
                verified[package_type] = url

    # Der DLSS Manager liest das erste passende Feld in JSON-Reihenfolge.
    fields = (("desktopOnlyUrl", "desktop"), ("notebookUrl", "notebook"), ("directUrl", "desktop-notebook"))
    return {field: verified[package] for field, package in fields if package in verified}


def discover_official():
    pages = set()
    for href in links(fetch_text(SUPPORT_SEARCH)):
        parsed = urlsplit(urljoin(SUPPORT_SEARCH, href))
        match = SUPPORT_PATH.match(parsed.path)
        if parsed.scheme == "https" and parsed.netloc == "nvidia.custhelp.com" and match:
            pages.add(f"https://nvidia.custhelp.com{match[1]}")
    if not pages:
        raise ValueError("keine NVIDIA-Supportartikel gefunden")

    entries = {}
    for page in sorted(pages):
        try:
            html = fetch_text(page)
        except (URLError, OSError) as exc:
            print(f"NVIDIA-Supportartikel fehlgeschlagen ({page}): {exc}")
            continue
        for heading in re.findall(r"<h1\b[^>]*>(.*?)</h1>", html, re.IGNORECASE | re.DOTALL):
            title = unescape(re.sub(r"<[^>]+>", "", heading)).strip()
            match = HOTFIX_TITLE.fullmatch(title)
            if match:
                entries[match[1]] = {"pageUrl": page, "summary": title, "installerUrls": list(links(html))}
    return entries


def discover_rss(url):
    entries = {}
    root = ET.fromstring(fetch_text(url))
    if root.find("channel") is None:
        raise ValueError("ungültiger RSS-Feed")
    for item in root.findall("./channel/item"):
        title = (item.findtext("title") or "").strip()
        match = HOTFIX_TITLE.search(title) or HOTFIX_SUFFIX.search(title)
        if not match:
            continue
        release_date = ""
        publication_date = item.findtext("pubDate")
        if publication_date:
            try:
                release_date = parsedate_to_datetime(publication_date).date().isoformat()
            except (ValueError, OverflowError):
                pass
        entries.setdefault(match[1], {
            "releaseDate": release_date,
            "pageUrl": (item.findtext("link") or "").strip(),
            "summary": title,
        })
    return entries


def discover_techpowerup():
    return {
        match[1]: {
            "pageUrl": TECHPOWERUP,
            "summary": f"NVIDIA GeForce Graphics Drivers {match[1]} Hotfix",
        }
        for match in HOTFIX_SUFFIX.finditer(fetch_text(TECHPOWERUP))
    }


def discover():
    discovered = {}
    successful_sources = 0
    sources = (
        ("NVIDIA Support", discover_official),
        ("Google News RSS", lambda: discover_rss(GOOGLE_RSS)),
        ("TechPowerUp", discover_techpowerup),
        ("Guru3D RSS", lambda: discover_rss(GURU3D_RSS)),
    )
    for name, fetch in sources:
        try:
            entries = fetch()
        except (URLError, OSError, ValueError, ET.ParseError) as exc:
            print(f"{name} fehlgeschlagen: {exc}")
            continue
        successful_sources += 1
        print(f"{name}: {len(entries)} Kandidaten")
        for version, entry in entries.items():
            candidate = discovered.setdefault(version, {})
            for key, value in entry.items():
                if value and not candidate.get(key):
                    candidate[key] = value
    if not successful_sources:
        raise RuntimeError("Keine Hotfix-Quelle konnte abgerufen werden.")
    return discovered


def version_key(entry):
    return tuple(map(int, entry["version"].split(".")))


def merge_hotfixes(existing, discovered):
    merged = {entry["version"]: entry.copy() for entry in existing}
    for version, candidate in discovered.items():
        previous = merged.get(version)
        downloads = verified_downloads(version, candidate.get("installerUrls", []))
        if previous is None and not downloads:
            print(f"Hotfix {version} übersprungen: kein bestätigter Installer.")
            continue

        if previous is None:
            title = f"GeForce Hotfix Display Driver version {version}"
            previous = merged[version] = {
                "version": version,
                "series": f"{version[0]}xx",
                "releaseDate": "",
                "title": title,
                "pageUrl": candidate.get("pageUrl", FORUM),
                "forumUrl": FORUM,
                "summary": candidate.get("summary", title),
                "sha256": "",
            }
        if downloads:
            for field in ("desktopOnlyUrl", "notebookUrl", "directUrl"):
                previous.pop(field, None)
            previous.update(downloads)
            if candidate.get("installerUrls"):
                previous["pageUrl"] = candidate["pageUrl"]
        if not previous.get("releaseDate") and candidate.get("releaseDate"):
            previous["releaseDate"] = candidate["releaseDate"]

    return sorted(merged.values(), key=version_key, reverse=True)[:25]


def main():
    raw_feed = OUTPUT_FILE.read_text(encoding="utf-8") if OUTPUT_FILE.exists() else ""
    old_feed = json.loads(raw_feed) if raw_feed.strip() else None
    existing = old_feed["hotfixes"] if old_feed is not None else []
    updated = merge_hotfixes(existing, discover())
    if old_feed is not None and updated == existing:
        print("Keine Änderungen an hotfixes.json.")
        return
    payload = {
        "schemaVersion": 1,
        "updatedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "hotfixes": updated,
    }
    OUTPUT_FILE.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"hotfixes.json mit {len(updated)} Einträgen aktualisiert.")


if __name__ == "__main__":
    main()
