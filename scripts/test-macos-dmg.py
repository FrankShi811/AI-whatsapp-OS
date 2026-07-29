from __future__ import annotations

import argparse
import os
import plistlib
import subprocess
import tempfile
from pathlib import Path


CPU_LABELS = {"arm64": "arm64", "x64": "x86_64"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="挂载 AI Sales OS DMG 并执行原生 macOS 运行时冒烟测试"
    )
    parser.add_argument("--path", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--architecture", choices=("arm64", "x64"), required=True)
    return parser.parse_args()


def run(
    *args: str,
    timeout: int = 120,
    check: bool = True,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        args,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        timeout=timeout,
        check=False,
        env=env,
    )
    if check and result.returncode != 0:
        raise RuntimeError(
            f"command failed ({result.returncode}): {' '.join(args)}\n{result.stdout}"
        )
    return result


def main() -> int:
    args = parse_args()
    dmg = Path(args.path).resolve()
    if not dmg.is_file() or dmg.stat().st_size < 10 * 1024 * 1024:
        raise RuntimeError(f"DMG 缺失或异常过小: {dmg}")
    attached = run(
        "/usr/bin/hdiutil",
        "attach",
        "-readonly",
        "-nobrowse",
        "-plist",
        str(dmg),
    )
    payload = plistlib.loads(attached.stdout.encode("utf-8"))
    mount_points = [
        entity.get("mount-point")
        for entity in payload.get("system-entities", [])
        if entity.get("mount-point")
    ]
    if len(mount_points) != 1:
        raise RuntimeError(f"无法确定 DMG 挂载点: {mount_points}")
    mount = Path(mount_points[0])
    try:
        app = mount / "AI Sales OS.app"
        plist_path = app / "Contents" / "Info.plist"
        executable = app / "Contents" / "MacOS" / "AISalesOS.Mac"
        bridge = app / "Contents" / "MacOS" / "WAFlow.WhatsApp.Bridge"
        if not plist_path.is_file() or not executable.is_file() or not bridge.is_file():
            raise RuntimeError("DMG 缺少 .app、主程序或 WhatsApp Bridge")
        with plist_path.open("rb") as stream:
            info = plistlib.load(stream)
        if info.get("CFBundleShortVersionString") != args.version:
            raise RuntimeError(
                f"DMG 版本错误: {info.get('CFBundleShortVersionString')}"
            )
        if info.get("CFBundleIdentifier") != "com.aisalesos.desktop":
            raise RuntimeError("DMG 应用身份错误")
        expected = CPU_LABELS[args.architecture]
        for path in (executable, bridge):
            file_result = run("/usr/bin/file", str(path))
            if expected not in file_result.stdout:
                raise RuntimeError(
                    f"{path.name} 架构不匹配，expected={expected}: {file_result.stdout}"
                )
            if not os.access(path, os.X_OK):
                raise RuntimeError(f"{path.name} 缺少执行权限")
        run(
            "/usr/bin/codesign",
            "--verify",
            "--deep",
            "--strict",
            "--verbose=2",
            str(app),
        )
        with tempfile.TemporaryDirectory(prefix="ai-sales-os-dmg-smoke-") as data_root:
            smoke_environment = os.environ.copy()
            smoke_environment["WAFLOW_LOCAL_APP_DATA_ROOT"] = data_root
            smoke = run(
                str(executable),
                "--smoke-test",
                timeout=180,
                env=smoke_environment,
            )
            if "PASS macOS runtime smoke" not in smoke.stdout:
                raise RuntimeError(f"运行时冒烟输出不完整:\n{smoke.stdout}")
            ui_smoke = run(
                str(executable),
                "--ui-smoke-test",
                timeout=60,
                env=smoke_environment,
            )
            if "PASS macOS UI smoke" not in ui_smoke.stdout:
                raise RuntimeError(f"窗口冒烟输出不完整:\n{ui_smoke.stdout}")
        print(
            f"PASS DMG version={args.version} arch={args.architecture} "
            f"size={dmg.stat().st_size / 1024 / 1024:.2f}MB"
        )
        print(smoke.stdout.strip())
        print(ui_smoke.stdout.strip())
    finally:
        run("/usr/bin/hdiutil", "detach", str(mount), check=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
