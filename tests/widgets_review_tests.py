from __future__ import annotations

import json
import tempfile
import zipfile
from pathlib import Path

MAX_ENTRIES = 500
MAX_UNCOMPRESSED = 200 * 1024 * 1024
FORBIDDEN = {".exe", ".dll", ".bat", ".cmd", ".ps1", ".com", ".scr", ".msi"}


def validate(path: Path) -> bool:
    if path.stat().st_size > 50 * 1024 * 1024:
        return False
    with zipfile.ZipFile(path) as archive:
        if len(archive.infolist()) > MAX_ENTRIES:
            return False
        if sum(max(0, item.file_size) for item in archive.infolist()) > MAX_UNCOMPRESSED:
            return False
        names = {item.filename.replace("\\", "/").lower() for item in archive.infolist()}
        if "manifest.json" not in names or "index.html" not in names:
            return False
        try:
            manifest = json.loads(archive.read(next(item for item in archive.infolist() if item.filename.lower() == "manifest.json")))
        except (StopIteration, json.JSONDecodeError):
            return False
        widget_id = manifest.get("id", "")
        if not isinstance(widget_id, str) or len(widget_id) < 3 or ".." in widget_id:
            return False
        if any(not (c.isalnum() or c in ".-_") for c in widget_id):
            return False
        seen = set()
        for item in archive.infolist():
            name = item.filename.replace("\\", "/")
            if name.lower() in seen or name.startswith("/") or ".." in name.split("/"):
                return False
            seen.add(name.lower())
            if Path(name).suffix.lower() in FORBIDDEN:
                return False
    return True


def make_zip(root: Path, name: str, entries: dict[str, bytes]) -> Path:
    path = root / name
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for filename, data in entries.items():
            archive.writestr(filename, data)
    return path


def main() -> None:
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        valid_manifest = json.dumps({"id": "review.widget", "name": "Review", "entryPoint": "index.html"}).encode()
        valid = make_zip(root, "valid.zip", {"manifest.json": valid_manifest, "index.html": b"<h1>safe</h1>", "assets/icon.txt": b"ok"})
        assert validate(valid), "valid widget rejected"

        traversal = make_zip(root, "traversal.zip", {"manifest.json": valid_manifest, "index.html": b"ok", "../escape.txt": b"bad"})
        assert not validate(traversal), "path traversal accepted"

        executable = make_zip(root, "executable.zip", {"manifest.json": valid_manifest, "index.html": b"ok", "payload.dll": b"bad"})
        assert not validate(executable), "executable payload accepted"

        duplicate = root / "duplicate.zip"
        with zipfile.ZipFile(duplicate, "w") as archive:
            archive.writestr("manifest.json", valid_manifest)
            archive.writestr("index.html", b"ok")
            archive.writestr("INDEX.HTML", b"duplicate")
        assert not validate(duplicate), "duplicate path accepted"

        invalid_manifest = make_zip(root, "invalid_manifest.zip", {"manifest.json": b"{}", "index.html": b"ok"})
        assert not validate(invalid_manifest), "invalid manifest accepted"

    source = Path("src/m4xmudovv")
    catalog = (source / "Services/WidgetCatalogService.cs").read_text()
    window = (source / "Services/WidgetWindow.cs").read_text()
    manager = (source / "Services/WidgetManager.cs").read_text()
    assert "MaxUncompressedBytes" in catalog
    assert "NavigationStarting" in window and "IsAllowedLocalUri" in window
    assert "ConfigureGamingMode" in manager and "HideRequested" in manager
    print("Widget security and lifecycle regression tests: PASS")


if __name__ == "__main__":
    main()
