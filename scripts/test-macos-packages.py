from __future__ import annotations

import argparse
import plistlib
import stat
import zipfile
from pathlib import Path


MACHO_CPU_TYPES = {
    "arm64": 0x0100000C,
    "x64": 0x01000007,
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="验证 AI Sales OS macOS 自包含中文应用包的版本、架构和权限"
    )
    parser.add_argument("--version", required=True)
    parser.add_argument("--installers", default="dist/installers")
    return parser.parse_args()


def find_one(names: list[str], suffix: str) -> str:
    matches = [name for name in names if name.endswith(suffix)]
    if len(matches) != 1:
        raise RuntimeError(f"期望且仅期望一个 {suffix}，实际为 {len(matches)} 个")
    return matches[0]


def validate_archive(path: Path, version: str, architecture: str) -> None:
    if not path.is_file() or path.stat().st_size < 10 * 1024 * 1024:
        raise RuntimeError(f"macOS 安装包缺失或异常过小: {path}")

    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        plist_name = find_one(names, "AI Sales OS.app/Contents/Info.plist")
        executable_name = find_one(
            names, "AI Sales OS.app/Contents/MacOS/AISalesOS.Mac"
        )
        guide_name = find_one(names, "安装说明.txt")

        plist = plistlib.loads(archive.read(plist_name))
        if plist.get("CFBundleShortVersionString") != version:
            raise RuntimeError(
                f"{path.name} 版本错误: {plist.get('CFBundleShortVersionString')}"
            )
        if plist.get("CFBundleVersion") != version:
            raise RuntimeError(f"{path.name} bundle version 与 {version} 不一致")
        if plist.get("CFBundleIdentifier") != "com.aisalesos.desktop":
            raise RuntimeError(f"{path.name} App 身份不稳定")
        if plist.get("CFBundleDevelopmentRegion") != "zh_CN":
            raise RuntimeError(f"{path.name} 不是中文默认区域")
        if "zh_CN" not in plist.get("CFBundleLocalizations", []):
            raise RuntimeError(f"{path.name} 缺少中文本地化声明")

        with archive.open(executable_name) as executable_stream:
            executable_header = executable_stream.read(8)
        if executable_header[:4] not in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"):
            raise RuntimeError(f"{path.name} 主程序不是 64 位 Mach-O")
        byte_order = (
            "little" if executable_header[:4] == b"\xcf\xfa\xed\xfe" else "big"
        )
        cpu_type = int.from_bytes(executable_header[4:8], byte_order)
        if cpu_type != MACHO_CPU_TYPES[architecture]:
            raise RuntimeError(
                f"{path.name} 架构错误: expected={architecture} cpu=0x{cpu_type:08X}"
            )

        unix_mode = archive.getinfo(executable_name).external_attr >> 16
        if not unix_mode & stat.S_IXUSR:
            raise RuntimeError(f"{path.name} 主程序缺少 Unix 执行权限")

        if not any(name.endswith("libhostfxr.dylib") for name in names):
            raise RuntimeError(f"{path.name} 缺少自包含 .NET hostfxr")
        if not any(name.endswith("libcoreclr.dylib") for name in names):
            raise RuntimeError(f"{path.name} 缺少自包含 .NET CoreCLR")

        guide = archive.read(guide_name).decode("utf-8-sig")
        for required_text in ("安装步骤", "首次启动", "中文测试版"):
            if required_text not in guide:
                raise RuntimeError(f"{path.name} 中文安装说明缺少: {required_text}")

    print(
        f"PASS  {path.name}: version={version} arch={architecture} "
        f"size={path.stat().st_size / 1024 / 1024:.2f}MB"
    )


def main() -> int:
    args = parse_args()
    installers = Path(args.installers).resolve()
    validate_archive(
        installers / "AI Sales OS macOS Apple-Silicon Chinese Preview.zip",
        args.version,
        "arm64",
    )
    validate_archive(
        installers / "AI Sales OS macOS Intel Chinese Preview.zip",
        args.version,
        "x64",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
