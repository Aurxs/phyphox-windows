#!/usr/bin/env python3
"""Build the Windows phone preview portable ZIP on any .NET 10 SDK host.

Uses the existing CreatePortableLauncher target and an isolated MSBuild artifacts
folder. No source obj/bin, user data, certificates, VM state or firewall changes.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def run(command: list[str], cwd: Path = ROOT) -> None:
    print("Running:", " ".join(command), flush=True)
    subprocess.run(command, cwd=cwd, env={**os.environ, "MSBUILDDISABLENODEREUSE": "1"}, check=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/phyphox-windows-phone-preview-win-x64.zip")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    parser.add_argument("--skip-web-build", action="store_true", help="Use the already verified web/dist build.")
    parser.add_argument("--nuget-source", help="Optional offline package source/cache directory.")
    parser.add_argument("--artifacts-path", type=Path, help="Isolated MSBuild output; default is a fresh temporary directory.")
    args = parser.parse_args()
    if not args.dotnet:
        parser.error(".NET SDK is required; specify --dotnet if not on PATH")
    archive = args.output.resolve()
    if archive.suffix.lower() != ".zip":
        parser.error("--output must be a ZIP file")
    if not args.skip_web_build:
        npm = shutil.which("npm")
        if not npm:
            parser.error("Node.js/npm is required unless --skip-web-build is used")
        run([npm, "ci"], ROOT / "web")
        run([npm, "run", "build"], ROOT / "web")
    for name in ("index.html", "phone.html", "phone-pcm-worklet.js"):
        if not (ROOT / "web/dist" / name).is_file():
            parser.error(f"Missing web/dist/{name}; build both web entries first")
    archive.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="phone-package-", dir=archive.parent) as stage_dir:
        stage = Path(stage_dir)
        portable = stage / "phyphox-windows-phone-preview-win-x64"
        app = portable / "app"
        app.mkdir(parents=True)
        intermediates = (args.artifacts_path or stage / "msbuild").resolve()
        common = ["-p:RuntimeIdentifier=win-x64", "-p:UseArtifactsOutput=true", f"-p:ArtifactsPath={intermediates}"]
        restore = [args.dotnet, "restore", "src/Phyphox.Server/Phyphox.Server.csproj", "-r", "win-x64", "--disable-parallel", "-m:1", "-p:NuGetAudit=false", "-p:SelfContained=true", *common]
        if args.nuget_source:
            restore += ["--source", args.nuget_source]
        run(restore)
        run([args.dotnet, "publish", "src/Phyphox.Server/Phyphox.Server.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "--no-restore", "-m:1", "-nr:false", "-p:UseSharedCompilation=false", *common, f"-p:PortableLauncherPath={portable / 'phyphox.exe'}", "-o", str(app)])
        for obsolete in app.rglob("opencv_videoio_ffmpeg*.dll"):
            obsolete.unlink()
        for file in ("LICENSE", "THIRD-PARTY-NOTICES.md", "README.md"):
            shutil.copy2(ROOT / file, app / file)
        shutil.copy2(ROOT / "tools/test-windows.ps1", app / "test-windows.ps1")
        shutil.copytree(ROOT / "docs", app / "docs", dirs_exist_ok=True)
        guide = ROOT / "user-guide/phyphox-Windows-使用指南.pdf"
        if guide.is_file():
            shutil.copy2(guide, portable / "使用指南.pdf")
        (portable / "开始使用.txt").write_text(
            "解压整个文件夹，然后双击 phyphox.exe。\n"
            "浏览器会自动打开；关闭浏览器不会停止后台，请通过任务栏窗口退出程序。\n"
            "app 文件夹包含运行时与静态页面，不要单独移动 EXE。首次启动会创建 data。\n"
            "先在实验库选择手机入门实验，按引导进入设置页的手机区域完成连接和输入绑定，再进入测量。\n"
            "完全离线，无需公网域名或云服务。手机默认直接打开本地 HTTPS 采集页。\n"
            "若浏览器提示连接安全问题，先核对电脑显示的 IP 地址，再按浏览器提示继续。\n"
            "仅在浏览器仍限制采集时使用说明中的可选证书兼容方案，不需要预先安装证书。\n"
            "操作步骤见 app/docs/PHONE-QUICKSTART.md，连接兼容说明见 PHONE-FIRST-CONNECTION.md。当前预览版同时绑定一个输入。\n"
            "Windows/Android/iPhone 传感器实机验收仍需完成；构建成功不代表硬件验证通过。\n"
            "更新请解压到新目录，退出旧程序后再复制旧 data，勿共享其中的私钥。\n", encoding="utf-8-sig")
        required = ["phyphox.exe", "app/Phyphox.Server.dll", "app/Phyphox.Pairing.dll", "app/wwwroot/index.html", "app/wwwroot/phone.html", "app/wwwroot/phone-pcm-worklet.js", "app/LICENSE", "app/THIRD-PARTY-NOTICES.md", "app/licenses/phone-qrcode/qrcode-MIT.txt", "app/docs/PHONE-FIRST-CONNECTION.md", "app/docs/PHONE-QUICKSTART.md"]
        for name in required:
            if not (portable / name).is_file():
                raise RuntimeError(f"Missing portable file: {name}")
        launcher = (portable / "phyphox.exe").read_bytes()
        pe_offset = struct.unpack_from("<I", launcher, 0x3C)[0]
        if launcher[:2] != b"MZ" or launcher[pe_offset:pe_offset + 4] != b"PE\0\0" or struct.unpack_from("<H", launcher, pe_offset + 4)[0] != 0x8664:
            raise RuntimeError("Portable launcher must be a Windows x64 PE executable")
        if (app / "Phyphox.Pairing.exe").exists() or (app / "Phyphox.Pairing.runtimeconfig.json").exists():
            raise RuntimeError("Unused standalone pairing host must not be delivered")
        paths = sorted(p for p in portable.rglob("*") if p.is_file())
        for path in paths:
            parts = path.relative_to(portable).parts
            if "data" in parts or "phone-certificates" in parts or path.suffix.lower() in {".pfx", ".p12", ".key"}:
                raise RuntimeError(f"Private/user material in package: {path.name}")
        manifest = [{"path": p.relative_to(portable).as_posix(), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in paths]
        (app / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        temporary_archive = stage / "package.zip"
        with zipfile.ZipFile(temporary_archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as package:
            for path in sorted(portable.rglob("*")):
                if path.is_file():
                    package.write(path, path.relative_to(stage).as_posix())
        with zipfile.ZipFile(temporary_archive) as package:
            bad = package.testzip()
            if bad:
                raise RuntimeError(f"Archive CRC failed: {bad}")
        os.replace(temporary_archive, archive)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(archive.suffix + ".sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii")
    print(f"Package: {archive}\nSHA256: {digest}\nBuild success is not Windows/device acceptance.")


if __name__ == "__main__":
    main()
