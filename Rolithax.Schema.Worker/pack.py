import argparse
from pathlib import Path
import subprocess
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


def main():
    root = Path(__file__).resolve().parent
    parser = argparse.ArgumentParser(description="Publish Windows and Linux Worker archives")
    parser.add_argument("--version", default="")
    parser.add_argument("--output", type=Path, default=root.parent / "Rolithax.Plugin.DaemonExtensions" / "dist")
    options = parser.parse_args()
    options.output.mkdir(parents=True, exist_ok=True)

    for runtime, launcher in (("win-x64", "Rolithax.Schema.Worker.exe"), ("linux-x64", "Rolithax.Schema.Worker")):
        publish = root / "bin" / "Release" / "net10.0" / runtime / "publish"
        command = [
            "dotnet", "publish", str(root / "Rolithax.Schema.Worker.csproj"),
            "-c", "Release", "-r", runtime, "--no-self-contained", "--nologo", "-o", str(publish),
        ]
        if options.version:
            command.append(f"-p:Version={options.version}")
        subprocess.run(command, check=True)

        output = options.output / f"Rolithax.Schema.Worker-{runtime}.zip"
        partial = output.with_suffix(".zip.tmp")
        files = (
            publish / launcher,
            publish / "Rolithax.Schema.Worker.dll",
            publish / "Rolithax.Schema.Worker.deps.json",
            publish / "Rolithax.Schema.Worker.runtimeconfig.json",
            root / "README.md",
        )
        try:
            with ZipFile(partial, "w") as archive:
                for source in files:
                    entry = ZipInfo.from_file(source, source.name)
                    entry.create_system = 3
                    mode = 0o100755 if runtime == "linux-x64" and source.name == launcher else 0o100644
                    entry.external_attr = mode << 16
                    archive.writestr(entry, source.read_bytes(), compress_type=ZIP_DEFLATED)
            partial.replace(output)
        finally:
            partial.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
