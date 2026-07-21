from __future__ import annotations

import argparse
import os
import plistlib
import shutil
import stat
import zipfile
from pathlib import Path

from PIL import Image


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="创建 AI Sales OS 原生 macOS 中文测试安装包")
    parser.add_argument("--publish", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--arch", choices=("arm64", "x64"), required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--icon", required=True)
    parser.add_argument("--bundle-output")
    return parser.parse_args()


def create_icon(source: Path, destination: Path) -> None:
    image = Image.open(source).convert("RGBA")
    side = max(image.size)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.alpha_composite(image, ((side - image.width) // 2, (side - image.height) // 2))
    canvas.resize((1024, 1024), Image.Resampling.LANCZOS).save(destination, format="ICNS")


def zip_tree(source: Path, destination: Path, executable_name: str) -> None:
    if destination.exists():
        destination.unlink()
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(source.rglob("*")):
            relative = path.relative_to(source.parent).as_posix()
            if path.is_dir():
                info = zipfile.ZipInfo(relative + "/")
                info.create_system = 3
                info.external_attr = (stat.S_IFDIR | 0o755) << 16
                archive.writestr(info, b"")
                continue
            executable = path.name == executable_name or path.suffix.lower() in {".dylib", ".so"}
            info = zipfile.ZipInfo.from_file(path, relative)
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | (0o755 if executable else 0o644)) << 16
            with path.open("rb") as stream:
                archive.writestr(info, stream.read(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def main() -> int:
    args = parse_args()
    publish = Path(args.publish).resolve()
    output = Path(args.output).resolve()
    icon = Path(args.icon).resolve()
    executable_name = "AISalesOS.Mac"
    executable = publish / executable_name
    if not executable.is_file():
        raise FileNotFoundError(f"macOS apphost 不存在: {executable}")

    architecture_name = "Apple 芯片" if args.arch == "arm64" else "Intel"
    staging_root = output.parent / f"macos-{args.arch}-staging"
    if staging_root.exists():
        shutil.rmtree(staging_root)
    staging_root.mkdir(parents=True)
    package_root = staging_root / f"AI Sales OS macOS {architecture_name} 中文测试版"
    package_root.mkdir()
    app = package_root / "AI Sales OS.app"
    macos = app / "Contents" / "MacOS"
    resources = app / "Contents" / "Resources"
    macos.mkdir(parents=True)
    resources.mkdir(parents=True)

    for item in publish.iterdir():
        target = macos / item.name
        if item.is_dir():
            shutil.copytree(item, target)
        else:
            shutil.copy2(item, target)

    create_icon(icon, resources / "AI-Sales-OS.icns")
    plist = {
        "CFBundleDevelopmentRegion": "zh_CN",
        "CFBundleDisplayName": "AI Sales OS",
        "CFBundleExecutable": executable_name,
        "CFBundleIconFile": "AI-Sales-OS.icns",
        "CFBundleIdentifier": "com.aisalesos.desktop",
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleLocalizations": ["zh_CN"],
        "CFBundleName": "AI Sales OS",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": args.version,
        "CFBundleVersion": args.version,
        "LSMinimumSystemVersion": "11.0",
        "NSHighResolutionCapable": True,
        "NSPrincipalClass": "NSApplication",
        "NSSupportsAutomaticGraphicsSwitching": True,
    }
    with (app / "Contents" / "Info.plist").open("wb") as stream:
        plistlib.dump(plist, stream, sort_keys=False)

    guide = f"""AI Sales OS {args.version} · macOS {architecture_name}中文测试版

安装步骤
1. 解压本压缩包。
2. 将“AI Sales OS.app”拖入“应用程序”文件夹。
3. 首次启动请右键应用，选择“打开”。
4. 如果 macOS 仍阻止启动，请在“系统设置 → 隐私与安全性”中选择“仍要打开”。
5. 仅在系统仍拦截且你确认文件来自本项目时，可在终端执行：
   xattr -dr com.apple.quarantine "/Applications/AI Sales OS.app"

本轮人工验收范围
- 原生 macOS 窗口和全中文界面
- Dashboard、商机智能、客户列表和本地历史数据读取
- API Key 写入 macOS 钥匙串
- 各模块导航与空状态

当前限制
- 本包尚未使用 Apple Developer ID 签名和公证。
- WhatsApp 扫码、实时收发、建群及群发依赖原生 macOS Bridge，本测试包默认不执行真实发送。
- Windows 数据不会跨电脑自动同步。macOS 数据库位置：
  ~/Library/Application Support/WAFlow/waflow.db
"""
    (package_root / "安装说明.txt").write_text(guide, encoding="utf-8-sig")

    if args.bundle_output:
        bundle_output = Path(args.bundle_output).resolve()
        if bundle_output.exists():
            shutil.rmtree(bundle_output)
        bundle_output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(app, bundle_output)

    output.parent.mkdir(parents=True, exist_ok=True)
    zip_tree(package_root, output, executable_name)
    shutil.rmtree(staging_root)
    print(f"Created: {output}")
    print(f"Architecture: {args.arch}")
    print(f"Version: {args.version}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
