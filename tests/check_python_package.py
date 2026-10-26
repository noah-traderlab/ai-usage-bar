"""Check real distribution contents and isolated installation without user data."""
import pathlib
import subprocess
import sys
import tempfile
import venv
import zipfile

root = pathlib.Path(__file__).resolve().parents[1]
wheels = list((root / "dist").glob("*.whl"))
assert len(wheels) == 1, "Build into an empty dist directory"
wheel = wheels[0]
with zipfile.ZipFile(wheel) as archive:
    names = archive.namelist()
    modules = {"aiusagebar/" + p.name for p in (root / "mac/aiusagebar").glob("*.py")}
    assert modules.issubset(names), "Missing runtime module"
    for name in names:
        assert ".." not in pathlib.PurePosixPath(name).parts
        assert not name.startswith("/")
        assert name in modules or ".dist-info/" in name, name
    assert any(name.endswith("/licenses/LICENSE") for name in names)
    assert any(name.endswith("/licenses/THIRD_PARTY_NOTICES.md") for name in names)

with tempfile.TemporaryDirectory(prefix="aiusagebar-wheel-") as folder:
    env = pathlib.Path(folder) / "env"
    venv.create(env, with_pip=True)
    scripts = env / ("Scripts" if sys.platform == "win32" else "bin")
    python = scripts / ("python.exe" if sys.platform == "win32" else "python")
    cli = scripts / ("aiusagebar.exe" if sys.platform == "win32" else "aiusagebar")
    subprocess.run([str(python), "-m", "pip", "install", "--no-index", "--no-deps", str(wheel)], check=True, cwd=folder)
    for command in ([str(cli), "--help"], [str(python), "-I", "-X", "utf8", "-m", "aiusagebar", "--help"]):
        result = subprocess.run(command, check=True, cwd=folder, capture_output=True, timeout=30)
        assert b"join" in result.stdout and b"status" in result.stdout
    result = subprocess.run([str(python), "-I", "-X", "utf8", "-c", "import aiusagebar; print(aiusagebar.__version__)"], check=True, cwd=folder, capture_output=True, timeout=30)
    assert result.stdout.strip() == b"0.1.0"
print("Wheel contents, licenses, offline install, console/module help and version passed")

