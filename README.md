# obr2sse

Converts Oblivion Remastered weapons into Skyrim Special Edition: meshes, textures, and a plugin.

No game assets are shipped. The tool reads both games from your own installs and writes a folder you
install with a mod manager. The output isn't redistributable (it mixes files from both games); only the
tool is.

## Scope

Melee weapons and staves, with their scabbards, in first and third person, blood decals carried over.
Skinned meshes (bows, worn armour) are skipped.

## App

`Obr2SseApp` is the front end most people use: one window that finds both games, offers the standalone
or replacer mode and a zip or loose output, and runs the conversion. The Oblivion mappings and the
replacer list are bundled, so nothing has to be supplied.

Build the distributable with `Obr2SseApp\publish.cmd`. It produces a single self-contained
`Obr2Sse.exe` (runtime, native library and texconv bundled) that runs with no .NET installed and
nothing beside it. Everything below is the command-line tool it is built on.

## Requirements

- .NET 10 SDK
- [CUE4Parse](https://github.com/FabianFG/CUE4Parse), cloned as a sibling of this repo
- CMake, Ninja, and MSVC (Visual Studio 2022) to build the native NIF library
- texconv, downloaded during build

## Building

Clone CUE4Parse next to this repo (the projects reference `..\..\CUE4Parse`):

```
git clone https://github.com/FabianFG/CUE4Parse.git ../CUE4Parse
```

Build the native library, from an x64 Native Tools Command Prompt:

```
git clone https://github.com/ousnius/nifly.git native/nifly
cd native
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

Get texconv into `tools/`:

```
curl -L -o tools/texconv.exe https://github.com/microsoft/DirectXTex/releases/latest/download/texconv.exe
```

Then build the tool:

```
cd Obr2Sse
dotnet build -c Release
```

`nifwrap.dll` and `texconv.exe` are copied next to the build output.

## Usage

```
obr2sse convert <skyrim> <oblivion> <mappings.usmap> <outdir>
```

This is the standalone build. It sweeps every Oblivion weapon into its own mesh under `meshes\obr2sse`
and writes `OBR2SSE - Weapons.esp`, which adds each as a new craftable item. Nothing vanilla is
overwritten, so it installs alongside anything else.

Stats come from the vanilla weapon of the same material and type, so a glass sword grades like a glass
sword. Oblivion-only materials map to the nearest Skyrim tier (Amber and Golden Saint to glass, Madness
to ebony, and so on). Named artifacts keep their own stats, enchantment and description: Mehrunes' Razor,
Chillrend, Goldbrand, the Ebony Blade, Wabbajack, the Skull of Corruption. Staves become weapon records
with a matching staff enchantment. The plugin is ESL-flagged and needs only `Skyrim.esm`.

### Options

- `--vanilla`, `--balanced` - simplify meshes to about 4k / 10k triangles (default keeps full detail)
- `--bc7` - slower, higher-quality textures
- `--no-fit` - keep Oblivion's authored length instead of scaling to the Skyrim template
- `--flip-v`, `--report`
- `--replacer <weapons.json>` - experimental, see below

### Replacer (experimental)

`--replacer` overwrites the vanilla meshes listed in `weapons.json`, so every instance of a weapon takes
on the imported look. The mapping is a rough, hand-checked guess, not a validated set. Prefer the
standalone build.

## Other commands

`catalog`, `espinfo`, `nodes`, `compare`, `probe`, `list`, `assets` and a few more are diagnostics for
inspecting assets, meshes, and the generated plugin. Run `obr2sse` with no arguments for the full list.

## Layout

```
native/nifly     nifly (cloned separately)
native/wrapper   C wrapper over nifly, built as nifwrap.dll
tools            texconv.exe (downloaded separately)
Obr2Sse          the command-line tool
Obr2SseApp       the GUI front end, published as a single Obr2Sse.exe
weapons.json     replacer mapping (only used by --replacer)
```

## Licence

GPL-3, since it links nifly.
