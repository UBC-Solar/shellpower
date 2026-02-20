from shellpower.internal import DotnetArchitecture, DotnetRID
import importlib
import pathlib
import enum
import sys


class ShellPowerExecutableType(enum.StrEnum):
    Worker = "ShellPower.Worker"
    Core = "ShellPower.Core"


class ShellPowerExecutable:
    def __init__(self, executable_type: ShellPowerExecutableType):
        self._rid: DotnetRID = DotnetArchitecture.dotnet_rid()
        self._executable_path: pathlib.Path = self._try_link_to_executable(executable_type)

    def _try_link_to_executable(self, executable_type: ShellPowerExecutableType) -> pathlib.Path:
        filepath = pathlib.Path(__file__).resolve().absolute()

        match executable_type:
            case ShellPowerExecutableType.Worker:
                executable_path = filepath.parent.parent.parent / "src" / str(executable_type) / "bin" / "Release" / "net9.0" / str(self._rid)

                if self._rid.system == "win" and executable_type:
                    executable_name = "ShellPower.Worker.exe"
                else:
                    executable_name = "ShellPower.Worker"

                build_executable = executable_path / executable_name

            case ShellPowerExecutableType.Core:
                build_executable = filepath.parent.parent.parent / "src" / str(executable_type) / "bin" / "Release" / "net9.0"

            case _:
                raise ValueError(f"Unsupported ShellPowerExecutableType to link to: {str(executable_type)}")

        if not pathlib.Path(build_executable).exists():
            raise RuntimeError(f"ShellPower.Worker has not been built for this system! \n"
                               f"Expected to find the file: {build_executable}\n"
                               f"Please navigate to the `src` folder and run \n dotnet publish ShellPower.Worker -c "
                               f"Release -r {self._rid}  --self-contained false -p:PublishSingleFile=true ")

        return build_executable.resolve().absolute()

    @property
    def path(self) -> pathlib.Path:
        return self._executable_path


class ShellPowerWorker(ShellPowerExecutable):
    def __init__(self):
        super().__init__(ShellPowerExecutableType.Worker)

    def __repr__(self):
        return f"ShellPower.Worker executable for {self._rid} at: {self._executable_path}"


class ShellPowerCore(ShellPowerExecutable):
    def __init__(self):
        super().__init__(ShellPowerExecutableType.Core)
        from pythonnet import load
        import os

        # Use the RID determined by your helper class
        rid_folder = str(self._rid) # e.g., "ubuntu.24.04-x64"

        filepath = pathlib.Path(__file__).resolve().absolute()
        # Navigate to the specific RID folder where the actual binaries live
        bin_dir = filepath.parent.parent.parent / "src" / "ShellPower.Worker" / "bin" / "Release" / "net9.0" / rid_folder

        config_path = bin_dir / "ShellPower.Worker.runtimeconfig.json"

        if not config_path.exists():
            raise FileNotFoundError(f"Critical: .NET config not found at {config_path}. "
                                    f"Did you run 'dotnet publish -r {rid_folder}'?")

        # Load the runtime
        load("coreclr", runtime_config=str(config_path))

        import clr  # noqa, clr needs to be imported after being loaded

        sys.path.append(str(self._executable_path))

        clr.AddReference("SSCP.ShellPower.Core")
        self._library = importlib.import_module("SSCP.ShellPower")

    @property
    def library(self):
        return self._library

    def __repr__(self):
        return f"ShellPower.Core library for {self._rid} at: {self._executable_path}"


if __name__ == "__main__":
    ex = ShellPowerExecutable(ShellPowerExecutableType.Worker)
