from typing import Optional
import subprocess
import platform
import os


class DotnetRID:
    def __init__(self, system: str, machine: str):
        self._system = system
        self._machine = machine

    @property
    def system(self) -> str:
        return self._system

    @property
    def machine(self) -> str:
        return self._machine

    def __str__(self) -> str:
        return f"{self._system}-{self._machine}"

    def __repr__(self) -> str:
        return self.__str__()


class DotnetArchitecture:
    @staticmethod
    def _is_musl_linux() -> bool:
        """
        Determine if using musl Linux as opposed to glibc (GNU).

        :return: `True` if using musl Linux as opposed to glibc (GNU).
        """
        # Works on Alpine; returns ('musl', version) on musl
        libc, _ = platform.libc_ver()
        if libc.lower() == "musl":
            return True

        # Fallback: common Alpine file
        try:
            return os.path.exists("/etc/alpine-release")
        except Exception:
            return False

    @staticmethod
    def _is_rosetta() -> bool:
        """
        When using macOS, detect if running under Rosetta (Apple Silicon emulating x86_64)

        :return: `True` if running under Rosetta, `False` otherwise
        """
        if platform.system() != "Darwin":
            return False
        try:
            out = subprocess.check_output(
                ["sysctl", "-in", "sysctl.proc_translated"],
                stderr=subprocess.DEVNULL,
                text=True,
            ).strip()
            return out == "1"
        except Exception:
            return False

    @staticmethod
    def _normalize_machine(m: str) -> str:
        m = (m or "").lower()
        if m in ("x86_64", "amd64"):
            return "x86_64"
        if m in ("aarch64", "arm64"):
            return "arm64"
        if m in ("i386", "i686", "x86"):
            return "x86"
        return m  # "armv7l", "armv6l", etc.

    @staticmethod
    def dotnet_rid(system: str | None = None, machine: str | None = None) -> DotnetRID:
        """
        Return a .NET Runtime Identifier (RID) suitable for `dotnet publish -r`.

        Currently supports,
        1. Windows
            a. x86_64
            b. x64
            c. arm64
        2. MacOS
            a. arm64
            b. x86_64 (if running under Rosetta, will target arm64)
        3. Linux
            a. linux-glibc (GNU Linux)
                i.   x86_64
                ii.  arm64
                iii. arm32
            b. linux-musl (musl Linux)
                i.   x86_64
                ii.  arm64
                iii. arm32

        :raises ValueError: If the system or machine cannot be identified.
        """
        sysname = (system or platform.system())
        mach = DotnetArchitecture._normalize_machine(machine or platform.machine())

        if sysname == "Windows":
            if mach == "x86_64":
                return DotnetRID("win", "x64")
            if mach == "arm64":
                return DotnetRID("win", "arm64")
            if mach == "x86":
                return DotnetRID("win", "x86")
            raise ValueError(f"Unsupported Windows architecture: {mach}")

        if sysname == "Linux":
            musl = DotnetArchitecture._is_musl_linux()
            base = "linux-musl" if musl else "linux"
            if mach == "x86_64":
                return DotnetRID(base, "x64")
            if mach == "arm64":
                return DotnetRID(base, "arm64")
            if mach in ("armv7l", "armv6l", "arm"):
                # .NET uses 'arm' for 32-bit ARM
                return DotnetRID(base, "arm")

            raise ValueError(f"Unsupported Linux architecture: {mach}")

        if sysname == "Darwin":
            # Correct for Rosetta: prefer native arm64 if running translated
            if DotnetArchitecture._is_rosetta():
                # Likely Apple Silicon; target native output
                return DotnetRID("osx", "arm64")
            if mach == "arm64":
                return DotnetRID("osx", "arm64")
            if mach == "x86_64":
                return DotnetRID("osx", "x64")

            raise ValueError(f"Unsupported macOS architecture: {mach}")

        raise ValueError(f"Unsupported OS: {sysname}")

    @staticmethod
    def build_dotnet_publish_cmd(
        project: str,
        configuration: str = "Release",
        single_file: bool = True,
        self_contained: bool = True,
        rid: Optional[str | DotnetRID] = None,
        extra_props: Optional[dict[str, str | bool]] = None,
    ) -> list[str]:
        rid = rid or DotnetArchitecture.dotnet_rid()
        props = {
            "PublishSingleFile": "true" if single_file else "false",
            "SelfContained": "true" if self_contained else "false",
        }

        if extra_props:
            for k, v in extra_props.items():
                props[k] = "true" if isinstance(v, bool) and v else ("false" if isinstance(v, bool) else str(v))

        cmd = [
            "dotnet", "publish", project,
            "-c", configuration,
            "-r", str(rid),
        ]

        for k, v in props.items():
            cmd.append(f"-p:{k}={v}")

        return cmd
