"""
Signs the macOS binaries hidden inside a jar, so Apple's notary accepts the app that contains it.

bundletool-all.jar carries a macOS aapt2 and JNA's libjnidispatch.jnilib. Apple's notarization looks inside zip-like
files and rejects the whole app if it finds an unsigned Mach-O binary ("The binary is not signed"). The jar has no
jar-signature, so it is safe to rewrite: every entry is copied unchanged except those binaries, which are signed
with the same Developer ID certificate as the app.

Usage: python prepare_jar.py in.jar out.jar --rcodesign rcodesign.exe --p12 id.p12 --p12-password-file pw.txt
"""

import argparse
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

# First four bytes of Mach-O binaries. ".class" files start with CAFEBABE too, so those are skipped by name.
MACHO_MAGICS = {bytes.fromhex(h) for h in ("cafebabe", "bebafeca", "feedface", "feedfacf", "cefaedfe", "cffaedfe")}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("jar")
    parser.add_argument("out")
    parser.add_argument("--rcodesign", required=True)
    parser.add_argument("--p12", required=True)
    parser.add_argument("--p12-password-file", required=True)
    args = parser.parse_args()

    signed = []
    with tempfile.TemporaryDirectory() as tmp, \
            zipfile.ZipFile(args.jar) as zin, \
            zipfile.ZipFile(args.out, "w") as zout:
        for info in zin.infolist():
            data = zin.read(info)
            if not info.is_dir() and not info.filename.endswith(".class") and data[:4] in MACHO_MAGICS:
                source = Path(tmp) / "in" / info.filename
                target = Path(tmp) / "out" / info.filename
                source.parent.mkdir(parents=True, exist_ok=True)
                target.parent.mkdir(parents=True, exist_ok=True)
                source.write_bytes(data)

                result = subprocess.run(
                    [args.rcodesign, "sign", "--p12-file", args.p12, "--p12-password-file", args.p12_password_file,
                     "--for-notarization", str(source), str(target)],
                    capture_output=True, text=True)
                if result.returncode != 0:
                    sys.exit(f"Signing {info.filename} failed:\n{result.stdout}\n{result.stderr}")

                data = target.read_bytes()
                signed.append(info.filename)
            zout.writestr(info, data, compress_type=info.compress_type)

    print("Signed inside the jar: " + (", ".join(signed) if signed else "nothing (no macOS binaries found)"))


if __name__ == "__main__":
    main()
