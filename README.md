# Button Box for Simulations

A Windows app that turns phones and tablets on your local network into a digital button box for driving and truck simulators. All configuration happens on Windows; the phone is only the button box. Version 1 targets iPhone and iPad.

## Status

Early development. Nothing is usable yet and there are no releases.

## Planned game support

- Assetto Corsa, Assetto Corsa Competizione and Assetto Corsa EVO, through vJoy
- Euro Truck Simulator 2 and American Truck Simulator, through an SCS SDK plugin

## Requirements to build

- Windows 11
- Git
- .NET 10 SDK (the exact version is pinned in `global.json`)
- Node.js 24
- pnpm (the version is pinned by `packageManager` in `src/web/package.json`)
- Visual Studio 2026 Build Tools with the C++ toolset, the "C++ CMake tools" and the "C++ Clang tools" components (run the plugin commands from a Developer PowerShell for VS 2026, or put CMake on `PATH`)

## Build, test and lint

Build the web apps first: the host build copies their output.

```powershell
# Web (from src/web)
cd src/web
pnpm install --frozen-lockfile
pnpm build
pnpm typecheck
pnpm lint
pnpm test
cd ../..
```

```powershell
# Host
dotnet build src/host/ButtonBox.slnx -c Release
dotnet test src/host/ButtonBox.slnx -c Release
dotnet format src/host/ButtonBox.slnx --verify-no-changes
```

```powershell
# SCS plugin
cmake -S src/plugins/scs -B src/plugins/scs/build -G "Visual Studio 18 2026" -A x64
cmake --build src/plugins/scs/build --config Release
cmake --build src/plugins/scs/build --config Release --target format-check
```

If CMake cannot find `clang-format`, pass `-DCLANG_FORMAT_EXECUTABLE=<path to clang-format.exe>` when configuring. The Build Tools ship it under `VC\Tools\Llvm\x64\bin`.

## Repository layout

- `src/host`: Windows host app (.NET)
- `src/web`: phone client and editor (TypeScript, pnpm workspace)
- `src/plugins/scs`: SCS SDK plugin for the truck simulators (C++)
- `schemas`: JSON schemas shared by the host and the web apps
- `assets`: logo and other project assets

## License

MIT, see [LICENSE](LICENSE). Third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). To report a security issue, see [SECURITY.md](SECURITY.md).
