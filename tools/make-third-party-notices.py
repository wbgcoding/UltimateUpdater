#!/usr/bin/env python3
"""Writes THIRD-PARTY-NOTICES.txt at the repo root.

The file carries, verbatim, the licence texts of everything that ships inside the exe: the NuGet
packages the app actually uses (read from src/UltimateUpdater/obj/project.assets.json after a restore) and the
self-contained .NET runtime. The texts come from the NuGet package cache, never from memory. A package that
ships no licence file in its package is listed with its licence name and URL instead.

Run it after a package update:  python tools/make-third-party-notices.py
Needs a restored src/UltimateUpdater project and the packages in the NuGet cache (NUGET_PACKAGES or
~/.nuget/packages). A test checks the result against the project's PackageReference list.
"""
import hashlib
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "UltimateUpdater" / "obj" / "project.assets.json"
OUT = ROOT / "THIRD-PARTY-NOTICES.txt"
CACHE = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")

RUNTIME_PACKAGES = [
    ("Microsoft.NETCore.App.Runtime.win-x64", ".NET runtime (core libraries)"),
    ("Microsoft.WindowsDesktop.App.Runtime.win-x64", ".NET desktop runtime (Windows Presentation Foundation and Windows Forms)"),
]

LICENSE_NAME = re.compile(r"^(licen[sc]e|copying)(\.(md|txt))?$", re.IGNORECASE)
NOTICE_NAME = re.compile(r"^third[-_ ]?party[-_ ]?notices?(\.(md|txt))?$", re.IGNORECASE)


def read(path: Path) -> str:
    text = path.read_bytes().decode("utf-8-sig", errors="replace")
    text = text.replace("\r\n", "\n").replace("\r", "\n").strip("\n")
    # The repository ships no em or en dashes; the two in the upstream prose become a plain hyphen, nothing
    # else in a text is altered.
    return text.replace("\u2014", "-").replace("\u2013", "-")


def nuspec_info(folder: Path):
    spec = next(folder.glob("*.nuspec"), None)
    info = {"license": "", "url": "", "authors": "", "copyright": ""}
    if spec is None:
        return info
    ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
    meta = ET.parse(spec).getroot().find("n:metadata", ns)
    if meta is None:
        return info
    for key, tag in (("license", "license"), ("url", "projectUrl"), ("authors", "authors"), ("copyright", "copyright")):
        node = meta.find(f"n:{tag}", ns)
        if node is not None and node.text:
            info[key] = node.text.strip()
    return info


def shipped_packages():
    assets = json.loads(ASSETS.read_text(encoding="utf-8"))
    target = next(t for name, t in assets["targets"].items() if name.endswith("/win-x64"))
    result = []
    for key, value in sorted(target.items()):
        if value.get("type") != "package":
            continue
        files = list(value.get("compile", {})) + list(value.get("runtime", {}))
        if any(not f.endswith("_._") for f in files):
            pid, _, version = key.partition("/")
            result.append((pid, version))
    return result


def runtime_version():
    assets = json.loads(ASSETS.read_text(encoding="utf-8"))
    for project in assets["project"]["frameworks"].values():
        for dep in project.get("downloadDependencies", []):
            if dep["name"] == "Microsoft.NETCore.App.Runtime.win-x64":
                return dep["version"].strip("[]").split(",")[0].strip()
    raise SystemExit("runtime version not found in project.assets.json")


def section(title: str, folder: Path, seen: set):
    info = nuspec_info(folder)
    lines = ["=" * 78, title, "=" * 78, ""]
    if info["license"]:
        lines.append(f"Licence: {info['license']}")
    if info["url"]:
        lines.append(f"Project: {info['url']}")
    if info["copyright"]:
        lines.append(f"Copyright: {info['copyright']}")
    elif info["authors"]:
        lines.append(f"Authors: {info['authors']}")
    lines.append("")
    texts = []
    has_license_file = False
    for entry in sorted(folder.iterdir()):
        if entry.is_file() and (LICENSE_NAME.match(entry.name) or NOTICE_NAME.match(entry.name)):
            has_license_file = has_license_file or bool(LICENSE_NAME.match(entry.name))
            body = read(entry)
            digest = hashlib.sha256(body.encode("utf-8")).hexdigest()
            if digest in seen:
                lines.append(f"(The text of {entry.name} is the same as one printed above.)")
                lines.append("")
                continue
            seen.add(digest)
            texts.append((entry.name, body))
    if not has_license_file:
        if title.startswith("System."):
            lines.append(
                "This package comes from the .NET runtime repository and is licensed under the MIT licence. "
                "Its licence text is the .NET runtime licence printed in the first section of this file.")
        else:
            url = f"https://licenses.nuget.org/{info['license']}" if info["license"] else "see the project page"
            lines.append(
                "This package ships no licence file. Its licence is "
                f"{info['license'] or 'not stated'} ({url}); the full text is available there.")
        lines.append("")
    for name, body in texts:
        lines.append(f"--- {name} ---")
        lines.append("")
        lines.append(body)
        lines.append("")
    return "\n".join(lines)


def main():
    if not ASSETS.exists():
        raise SystemExit("restore src/UltimateUpdater first (dotnet restore): project.assets.json is missing")
    version = runtime_version()
    seen: set = set()
    parts = [
        "THIRD-PARTY NOTICES",
        "",
        "Ultimate Updater itself is licensed under the MIT licence (see LICENSE). It includes the",
        "components below. Each notice is reproduced as the component's authors publish it.",
        "",
    ]
    for pid, label in RUNTIME_PACKAGES:
        folder = CACHE / pid.lower() / version
        if not folder.is_dir():
            raise SystemExit(f"runtime package not in the NuGet cache: {pid} {version}")
        parts.append(section(f"{label}, version {version}", folder, seen))
    for pid, ver in shipped_packages():
        folder = CACHE / pid.lower() / ver
        if not folder.is_dir():
            raise SystemExit(f"package not in the NuGet cache: {pid} {ver}")
        parts.append(section(f"{pid} {ver}", folder, seen))
    text = "\n".join(parts).rstrip("\n") + "\n"
    OUT.write_bytes(text.replace("\n", "\r\n").encode("utf-8"))
    print(f"wrote {OUT.name}: {len(text)} characters")


if __name__ == "__main__":
    sys.exit(main())
