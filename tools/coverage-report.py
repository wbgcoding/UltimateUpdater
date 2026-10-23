"""Reads a cobertura coverage report and prints the classes with the lowest line coverage.

Standard library only, no report-generator tool: coverlet already writes cobertura XML
(`dotnet test --collect:"XPlat Code Coverage"`), and this just reads it back. A handful of
directories are known to carry an elevated process, an untrusted download or a delete call, so a
low number there is worse than the same number in an ordinary settings class; those classes are
always listed first, however small the file, rather than sorted purely by coverage percentage.

Run:  python tools/coverage-report.py <path-to-coverage.cobertura.xml>
"""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# Matched against the class's source path (forward slashes, case-insensitive substring). Anything
# here is listed ahead of every other class regardless of its own coverage percentage.
PRIORITY_PATHS = [
    "Services/Cleanup",
    "Services/Security",
    "ElevatedWorker",
    "ElevatedHost",
    "FileDeletion",
    "TrustedBinary",
]


def is_priority(filename: str) -> bool:
    normalized = filename.replace("\\", "/").lower()
    return any(path.lower() in normalized for path in PRIORITY_PATHS)


def display_name(name: str) -> str:
    """Collapses a compiler-generated nested type (an async state machine, a closure's
    display class) into the real class it was generated from. Without this, most of the ten
    lowest entries are unreadable names like "ElevatedHost/<PumpAsync>d__24" instead of the
    class a person actually wrote and could add a test for; the async method's own lines still
    count, just toward the outer class."""
    marker = name.find("/<")
    return name[:marker] if marker != -1 else name


def class_entries(root: ET.Element):
    """One (name, filename, covered, valid) per real class, merging the duplicate <class>
    elements cobertura emits for a partial class that spans more than one file, and folding
    every compiler-generated nested type into the class it belongs to."""
    merged: dict[str, tuple[str, str, int, int]] = {}

    for class_el in root.iter("class"):
        name = display_name(class_el.get("name", ""))
        filename = class_el.get("filename", "")
        lines = class_el.findall("./lines/line")
        valid = len(lines)
        covered = sum(1 for line in lines if int(line.get("hits", "0")) > 0)

        if name in merged:
            prev_name, prev_filename, prev_covered, prev_valid = merged[name]
            merged[name] = (prev_name, prev_filename, prev_covered + covered, prev_valid + valid)
        else:
            merged[name] = (name, filename, covered, valid)

    return list(merged.values())


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: coverage-report.py <coverage.cobertura.xml>", file=sys.stderr)
        return 1

    xml_path = Path(argv[1])
    if not xml_path.is_file():
        print(f"not a file: {xml_path}", file=sys.stderr)
        return 1

    root = ET.parse(xml_path).getroot()
    overall_percent = round(float(root.get("line-rate", "0")) * 100, 1)

    ranked = []
    for name, filename, covered, valid in class_entries(root):
        if valid == 0:
            continue
        percent = covered / valid * 100
        ranked.append((not is_priority(filename), percent, name, filename, covered, valid))

    # Priority classes first (False sorts before True), then lowest coverage first within each group.
    ranked.sort(key=lambda entry: (entry[0], entry[1]))

    print(f"Overall line coverage: {overall_percent}%")
    print()
    print("Ten classes with the lowest line coverage (priority areas listed first):")
    for is_non_priority, percent, name, filename, covered, valid in ranked[:10]:
        marker = "priority" if not is_non_priority else "other"
        print(f"  [{marker}] {name} - {percent:.1f}% ({covered}/{valid} lines) - {filename}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
