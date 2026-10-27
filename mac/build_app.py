"""Build a self-contained .app and drag-to-Applications DMG on macOS."""
import hashlib
import pathlib
import platform
import shutil
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]


def run(*args):
    subprocess.run([str(a) for a in args], check=True, cwd=ROOT)


def main():
    if sys.platform != "darwin":
        raise SystemExit("Build this application on macOS.")
    arch = platform.machine()
    if arch not in ("arm64", "x86_64"):
        raise SystemExit(f"Unsupported architecture: {arch}")
    out = ROOT / "dist" / f"macos-{arch}"
    out.mkdir(parents=True, exist_ok=True)
    run(sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean",
        "--windowed", "--onedir", "--name", "AI Usage Bar",
        "--osx-bundle-identifier", "io.github.noah-traderlab.ai-usage-bar",
        "--paths", ROOT / "mac", "--distpath", out,
        "--workpath", ROOT / "build" / arch, "--specpath", ROOT / "build",
        "--add-data", f"{ROOT / 'LICENSE'}:licenses",
        "--add-data", f"{ROOT / 'THIRD_PARTY_NOTICES.md'}:licenses",
        ROOT / "mac" / "desktop_entry.py")
    app = out / "AI Usage Bar.app"
    run("codesign", "--verify", "--deep", "--strict", app)
    run(app / "Contents" / "MacOS" / "AI Usage Bar", "--smoke-test")
    stage = ROOT / "build" / f"dmg-{arch}"
    stage.mkdir(parents=True, exist_ok=True)
    staged_app = stage / app.name
    if staged_app.exists():
        raise SystemExit("DMG staging already exists; use a fresh checkout.")
    shutil.copytree(app, staged_app, symlinks=True)
    (stage / "Applications").symlink_to("/Applications", target_is_directory=True)
    shutil.copy2(ROOT / "docs" / "macos-app.md", stage / "READ-ME.md")
    dmg = out / f"AI-Usage-Bar-0.1.0-macos-{arch}.dmg"
    run("hdiutil", "create", "-volname", "AI Usage Bar", "-srcfolder", stage,
        "-ov", "-format", "UDZO", dmg)
    run("hdiutil", "verify", dmg)
    digest = hashlib.sha256(dmg.read_bytes()).hexdigest()
    (out / "SHA256SUMS.txt").write_text(f"{digest}  {dmg.name}\n", encoding="utf-8")


if __name__ == "__main__":
    main()
