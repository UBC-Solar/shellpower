# Shellpower Optimizer

## Overview

[_Shellpower_](../README.md) is an OpenGL-based C# simulation which simulates irradiance and shading on a curved solar array.
Shellpower was originally made over 12 years ago by the Stanford Solar Car Project team, and has been revamped by UBC Solar's
Simulation & Strategy team to support newer versions of .NET.

The _Shellpower Optimizer_ is a set of scripts designed to optimize the grouping of solar array cells into strings, referred to as the *array configuration*, for our latest solar car, Cascadia.

More internal documentation on UBC Solar's Monday.com board: https://ubcsolar26.monday.com/boards/9565353662/views/211183038/pulses/10058358397

## How it Works

This optimizer uses an iterative optimization approach loosely inspired by [Simulated Annealing](https://en.wikipedia.org/wiki/Simulated_annealing). The objective function to minimize is $-1 \cdot P_{avg}$, where $P_{avg} \equiv \sum{P_i}$ for each shading scenario $i$ specified in the configuration. In other words, we are trying to maximize the average predicted power over all shading scenarios.

To run the optimization, the following parameters must be specified as inputs:
- A 3D model for the top shell of the car (as an STL file)
- An initial texture file which encodes the cell locations and strings as a PNG file
- A set of location/datetime pairs to use for determinng shading scenarios
- A *temperature* for the optimization algorithm (see below)

A custom Python interface is used between the optimizer script (`main.py`) and the Shellpower library.
This is then wrapped by the Python `ArrayHandler` class which encapsulates the state of the simulator and provides the necessary methods to load & modify textures, simulate power and debug the optimization process.

## Running the Optimizer

### Software Setup

#### .NET / C#

The C# Shellpower program must be built in order to enable the simulation of power on the solar array.
Refer to the parent [Shellpower README](../README.md) for C# build instructions

> NOTE: The parent README does not yet explain the process to build Shellpower.
> Try navigating to the src folder and running `dotnet build` or similar,
> after [installing .NET](https://dotnet.microsoft.com/en-us/download).

#### Python

Astral-uv is used for dependency managment. Install [uv](https://docs.astral.sh/uv/getting-started/installation/), then run `uv sync` to set up your Python environment.
To run Python scripts with uv, use the command `uv run my_script.py`.

### Configuring inputs

A number of input parameters must bs set to complete an optimization run.
Parameters are set manually by changing constants at the top of [`main.py`](./main.py).

- **TOP_SHELL_MODEL**: A path to an STL file for the top geometry. For simulation, the texture file will be projected onto this file and then an OpenGL simulation determines the shading on the array in different solar conditions. Note than when exporting this model, ASCII must be selected. In addition, the units must be in mm. More info on [Monday.com](https://ubcsolar26.monday.com/boards/9565353662/pulses/10058358397/posts/4871112475).
- **BASE_TEXTURE_PATH**: A path to the texture file with which to start the simulation.
  - If this is your first time running the optimizer, a texture file must be created manually. A number of tools are available in the `texture_builder` directory to simplify this process. A scale of 1mm per pixel is helpful to keep dimensions simple and interpretable.
  - Texture files are interpreted based on the colour of each pixel:
    - Grayscale pixels are ignored and assumed to be part of the background. Any pixel with equivalent R, G and B channel values is considered grayscale.
    - Pixels on the same cell must be coloured with an identical RGB color.
    - Strings are indicated by the R and G channels. Each R+G channel value pair denotes a unique string (i.e., all cells in that string are considered to be in series). The remaining blue channel colour is used to distinguish cells on the same string. For example, pixels with colour RGB(1, 2, 3) and RGB(1, 2, 4) are considered to be in the same string but in different cells, whereas pixels RGB(1, 2, 3) and RGB(1, 3, 3) are different cells on different strings. See the monday board linked in the top of this document for more context.
- **INIT_TEMP**: The initial temperature for the simulated annealing model. The recommended value is 0, since testing has shown that a global minimum can be reached without the need for any backwards progress. More info on this parameter in [`simulated_annealing.py`](./simulated_annealing.py).
- **BYPASS_DIODES_JSON**: A file describing the bypass diodes in the array. Currently, a dummy file is used to represent no bypass diodes. The process to insert more is TBD.

### Running the Script

TODO: insert screenshots and a walkthrough of what it looks like to run the simulator