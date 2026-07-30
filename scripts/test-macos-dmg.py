from __future__ import annotations

import argparse
import os
import plistlib
import subprocess
import tempfile
from pathlib import Path


CPU_LABELS = {"arm64": "arm64", "x64": "x86_64"}
MACHO_CPU_TYPES = {"arm64": 0x0100000C, "x64": 0x01000007}
MACHO_CPU_SUBTYPES = {"arm64": 0, "x64": 3}


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


def validate_macho(path: Path, architecture: str) -> None:
    header = path.read_bytes()[:12]
    if header[:4] not in (b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf"):
        raise RuntimeError(f"{path.name} 不是 64 位 Mach-O")
    byte_order = "little" if header[:4] == b"\xcf\xfa\xed\xfe" else "big"
    cpu_type = int.from_bytes(header[4:8], byte_order)
    cpu_subtype = int.from_bytes(header[8:12], byte_order)
    if cpu_type != MACHO_CPU_TYPES[architecture]:
        raise RuntimeError(
            f"{path.name} 架构错误: expected={architecture} cpu=0x{cpu_type:08X}"
        )
    if cpu_subtype & 0x00FFFFFF != MACHO_CPU_SUBTYPES[architecture]:
        raise RuntimeError(
            f"{path.name} CPU subtype 错误: expected={architecture} "
            f"subtype=0x{cpu_subtype:08X}"
        )


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
        compatibility_launcher = mount / "首次安装并打开 AI Sales OS.command"
        if not plist_path.is_file() or not executable.is_file() or not bridge.is_file():
            raise RuntimeError("DMG 缺少 .app、主程序或 WhatsApp Bridge")
        if not compatibility_launcher.is_file() or not os.access(
            compatibility_launcher, os.X_OK
        ):
            raise RuntimeError("DMG 缺少可执行的首次安装兼容入口")
        launcher_text = compatibility_launcher.read_text(encoding="utf-8")
        for required in ("ditto", "xattr -dr com.apple.quarantine", "codesign --verify"):
            if required not in launcher_text:
                raise RuntimeError(f"首次安装兼容入口缺少安全步骤: {required}")
        with plist_path.open("rb") as stream:
            info = plistlib.load(stream)
        if info.get("CFBundleShortVersionString") != args.version:
            raise RuntimeError(
                f"DMG 版本错误: {info.get('CFBundleShortVersionString')}"
            )
        if info.get("CFBundleIdentifier") != "com.aisalesos.desktop":
            raise RuntimeError("DMG 应用身份错误")
        launch_architecture = CPU_LABELS[args.architecture]
        if info.get("LSArchitecturePriority") != [launch_architecture]:
            raise RuntimeError(
                "DMG LaunchServices 架构优先级错误: "
                f"{info.get('LSArchitecturePriority')}"
            )
        expected = CPU_LABELS[args.architecture]
        for path in (executable, bridge):
            validate_macho(path, args.architecture)
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
        run(
            "/usr/bin/codesign",
            "--display",
            "--verbose=4",
            str(app),
        )
        with tempfile.TemporaryDirectory(prefix="ai-sales-os-dmg-smoke-") as data_root:
            smoke_environment = os.environ.copy()
            smoke_environment["WAFLOW_DATABASE_PATH"] = str(
                Path(data_root) / "direct" / "waflow.db"
            )
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
            launch_root = Path(data_root) / "launchservices"
            launch_database = launch_root / "waflow.db"
            launch_result = launch_root / "result.txt"
            launch_capture = launch_root / "captures"
            launch_root.mkdir(parents=True, exist_ok=True)
            launch_variables = {
                "WAFLOW_DATABASE_PATH": str(launch_database),
                "WAFLOW_UI_SMOKE_RESULT_PATH": str(launch_result),
                "WAFLOW_UI_SMOKE_CAPTURE_DIR": str(launch_capture),
            }
            try:
                for name, value in launch_variables.items():
                    run("/bin/launchctl", "setenv", name, value)
                launch = run(
                    "/usr/bin/open",
                    "-W",
                    "-n",
                    str(app),
                    "--args",
                    "--ui-smoke-test",
                    timeout=120,
                )
                if not launch_result.is_file():
                    raise RuntimeError(
                        "LaunchServices 返回成功但应用没有写入 UI 冒烟结果，"
                        f"open output:\n{launch.stdout}"
                    )
                launch_text = launch_result.read_text(encoding="utf-8")
                if not launch_text.startswith("PASS macOS UI smoke"):
                    raise RuntimeError(f"Finder/LaunchServices 冒烟失败:\n{launch_text}")
                captures = sorted(launch_capture.glob("mac-*.png"))
                if len(captures) < 10:
                    raise RuntimeError(
                        f"Finder/LaunchServices UI 证据不足: {len(captures)} captures"
                    )
            finally:
                for name in launch_variables:
                    run("/bin/launchctl", "unsetenv", name, check=False)
        print(
            f"PASS DMG version={args.version} arch={args.architecture} "
            f"size={dmg.stat().st_size / 1024 / 1024:.2f}MB "
            "finder=LaunchServices"
        )
        print(smoke.stdout.strip())
        print(ui_smoke.stdout.strip())
        print(launch_text.strip())
    finally:
        run("/usr/bin/hdiutil", "detach", str(mount), check=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
