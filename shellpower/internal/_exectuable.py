from shellpower.internal import DotnetArchitecture, DotnetRID
import pathlib


class ShellPowerExecutable:
    def __init__(self):
        self._rid: DotnetRID = DotnetArchitecture.dotnet_rid()
        self._executable_path: pathlib.Path = self._try_link_to_executable()

    def _try_link_to_executable(self) -> pathlib.Path:
        filepath = pathlib.Path(__file__).resolve().absolute()
        build_executable = filepath.parent.parent.parent / "src" / "ShellPower.Worker" / "bin" / "Release" / "net9.0" / str(self._rid) / "ShellPower.Worker"

        if not pathlib.Path(build_executable).exists():
            raise RuntimeError(f"ShellPower.Worker has not been build for this system! \n"
                               f"Please navigate to the `src` folder and run \n dotnet publish ShellPower.Worker -c "
                               f"Release -r {self._rid}  --self-contained false -p:PublishSingleFile=true ")

        return build_executable.resolve().absolute()

    @property
    def path(self) -> pathlib.Path:
        return self._executable_path

    def __repr__(self):
        return f"ShellPower.Worker executable for {self._rid} at: {self._executable_path}"


if __name__ == "__main__":
    ex = ShellPowerExecutable()
