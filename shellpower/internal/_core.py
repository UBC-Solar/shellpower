from shellpower.internal._exectuable import ShellPowerCore

core = ShellPowerCore()
ShellPowerCore = core.library

__all__ = [
    "ShellPowerCore"
]
