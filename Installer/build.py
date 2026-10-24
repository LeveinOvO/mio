#!/usr/bin/env python3
"""Build a native NSIS installer from an existing, tested self-contained release."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
HERE = ROOT / "Installer"
sys.path.insert(0, str(ROOT / 'scripts'))
from version import version
VERSION = version()
LOCALE_DIRECTORY = re.compile(r"[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})+")


def supported_language(relative: Path) -> bool:
    """Filter culture directories, not similarly named runtime files/resources."""
    return all(not LOCALE_DIRECTORY.fullmatch(part) or part.split('-')[0].lower() in {'en', 'zh'}
               for part in relative.parts[:-1])


def quote(value: str) -> str:
    return value.replace('$', '$$').replace('"', '$\\"')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--test", action="store_true", help="Use separate registry/shortcut names for validation")
    parser.add_argument("--check-only", action="store_true", help="Validate input and output paths without building")
    args = parser.parse_args()
    release = ROOT / "artifacts" / "build" / ('V' + VERSION)
    required = ("Mio.exe", "Mio.Host.exe", "Mio.dll", "CoreBin/realm.exe")
    for name in required:
        if not (release / name).is_file():
            raise SystemExit(f"Missing release file: {release / name}")
    files = []
    excluded_language_files = []
    for path in sorted(release.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(release)
        if path.name.startswith('PortForwarder.Host') or path.name in {'PortForwarder.exe','PortForwarder.dll','PortForwarder.deps.json','PortForwarder.runtimeconfig.json','PortForwarder.pri'}:
            continue
        if (relative.parts[0].lower() == "data" or path.suffix.lower() in {".pdb", ".lnk", ".cmd", ".tmp"}
                or path.name.lower() == "uninstall.exe"):
            continue
        if path.is_symlink():
            raise SystemExit(f"Linked release file refused: {path}")
        if not supported_language(relative):
            excluded_language_files.append(path)
            continue
        files.append((path, relative))
    if args.check_only:
        destination = ROOT / "artifacts" / ("validation" if args.test else "releases") / ('V' + VERSION)
        print(f"Input: {release}\nOutput: {destination}\nPayload files: {len(files)}")
        print(f"Excluded other-language files: {len(excluded_language_files)}")
        return
    build = HERE / "build"
    build.mkdir(exist_ok=True)
    install, remove, checks, unchecks = [], [], [], []
    directories = set()
    for path, relative in files:
        windows = quote(str(relative).replace("/", "\\"))
        parent = str(relative.parent).replace("/", "\\")
        out = "$INSTDIR" if parent == "." else "$INSTDIR\\" + quote(parent)
        install += [f'SetOutPath "{out}"', f'File "{quote(str(path))}"']
        remove.append(f'Delete "$INSTDIR\\{windows}"')
        checks += [f'Push "$INSTDIR\\{windows}"', 'Call WaitForFile']
        unchecks += [f'Push "$INSTDIR\\{windows}"', 'Call un.WaitForFile']
        for directory in relative.parents:
            if str(directory) != ".":
                directories.add(directory)
    for directory in sorted(directories, key=lambda p: (-len(p.parts), str(p))):
        windows = quote(str(directory).replace("/", "\\"))
        remove.append(f'RMDir "$INSTDIR\\{windows}"')
    for name, lines in (("install-files", install), ("uninstall-files", remove),
                        ("check-files", checks), ("uncheck-files", unchecks)):
        (build / f"{name}.nsh").write_text("\n".join(lines) + "\n", encoding="utf-8")
    output_directory = ROOT / "artifacts" / ("validation" if args.test else "releases") / ('V' + VERSION)
    output_directory.mkdir(parents=True, exist_ok=True)
    output = output_directory / (f'Mio-Setup-{VERSION}' + ('-Validation' if args.test else '') + '.exe')
    command = ["makensis", "-V2", f"-DMIO_VERSION={VERSION}", f"-DOUTPUT_FILE={output}"]
    if args.test:
        command += ["-DVALIDATION_BUILD", "-DPRODUCT_NAME=Mio Installer Validation",
                    "-DREG_KEY=Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\MioInstallerValidation"]
    subprocess.run(command + [str(HERE / "Mio.nsi")], cwd=HERE, check=True)
    with output.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    output.with_suffix(output.suffix + ".sha256").write_text(f"{digest}  {output.name}\n", encoding="ascii")
    manifest = {}
    for path, relative in files:
        with path.open("rb") as stream:
            file_digest = hashlib.file_digest(stream, "sha256").hexdigest()
        manifest[relative.as_posix()] = {"bytes": path.stat().st_size, "sha256": file_digest}
    output.with_suffix('.payload.json').write_text(json.dumps({
        "version": VERSION, "languages": ["en", "zh"], "files": manifest
    }, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f"Packaged {len(files)} files, excluding user data.")
    print(f"Excluded {len(excluded_language_files)} other-language files "
          f"({sum(p.stat().st_size for p in excluded_language_files):,} uncompressed bytes).")
    print(f"{output} ({output.stat().st_size:,} bytes)\nSHA256 {digest}")


if __name__ == "__main__":
    main()
