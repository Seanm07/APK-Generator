"""
Zips a macOS .app keeping Unix permissions.

Windows zip tools (including PowerShell / .NET) drop the permission bits, which leaves the main binary non-executable
once it is unzipped on a Mac. This writes them explicitly. Same approach as zip_app() in the signing script.

Usage: python zip_app.py "path\\to\\APK Generator.app" "path\\to\\output.zip"
"""

import shutil
import sys
import time
import zipfile
from pathlib import Path

# First four bytes of Mach-O binaries (thin and universal): these need the executable permission on macOS
MACHO_MAGICS = {bytes.fromhex(h) for h in ("cafebabe", "bebafeca", "feedface", "feedfacf", "cefaedfe", "cffaedfe")}


def is_macho(path):
    try:
        with open(path, "rb") as f:
            return f.read(4) in MACHO_MAGICS
    except OSError:
        return False


def zip_app(app, zip_path):
    if zip_path.exists():
        zip_path.unlink()

    root = app.parent
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for path in sorted(app.rglob("*")):
            name = path.relative_to(root).as_posix()
            mtime = time.localtime(max(path.stat().st_mtime, 315532800))  # zip can't store dates before 1980

            if path.is_dir():
                info = zipfile.ZipInfo(name + "/", date_time=mtime[:6])
                info.external_attr = (0o040755 << 16) | 0x10
            else:
                info = zipfile.ZipInfo(name, date_time=mtime[:6])
                info.external_attr = (0o100755 if is_macho(path) else 0o100644) << 16
                info.compress_type = zipfile.ZIP_DEFLATED

            info.create_system = 3  # Unix, otherwise macOS ignores the permission bits
            if path.is_dir():
                zf.writestr(info, b"")
            else:
                with open(path, "rb") as src, zf.open(info, "w") as dst:
                    shutil.copyfileobj(src, dst, 1024 * 1024)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    zip_app(Path(sys.argv[1]), Path(sys.argv[2]))
