# BlockShot for Vintage Story

Catch something worth sharing? BlockShot sends screenshots and short clips straight to
[blocks.hot](https://blocks.hot), without making you leave the game.

## What it does

- Takes screenshots and records clips up to 30 seconds long
- Uploads automatically or asks first—your choice
- Copies the share link ready to paste
- Keeps recent uploads together, with previews and quick controls
- Supports anonymous uploads when you would rather keep your name off them
- Saves a local copy if an upload fails

It is entirely client-side, so the server does not need BlockShot installed.

## Getting started

Drop the ZIP into your Vintage Story `Mods` folder, load a world, then press `Ctrl+Shift+B` to
open BlockShot. Link your MineTogether account in the browser and you are ready to go.

The default keys are:

- `Ctrl+Shift+B` opens BlockShot.
- `Ctrl+Shift+S` takes a screenshot.
- `Ctrl+Shift+R` starts or stops video recording.

You can change all three from Vintage Story's controls menu. Clips are recorded at up to 1280×720
and 15 FPS, and currently contain video only.

## Installation

Copy `BlockShot-VintageStory-<version>.zip` into the Vintage Story `Mods` directory.

## Building

The project requires the .NET 10 SDK and a Vintage Story installation containing the 1.22.2 or
newer assemblies. Set `VINTAGE_STORY` to the game directory or pass it to the build script:

```powershell
dotnet test tests\BlockShot.VintageStory.Core.Tests\BlockShot.VintageStory.Core.Tests.csproj
.\build.ps1 -VintageStoryPath "C:\Path\To\Vintagestory"
```

The package is written to `artifacts/BlockShot-VintageStory-<version>.zip`.

## Data files

Configuration and local captures are stored in `VintagestoryData/BlockShot`.

## License

BlockShot is available under the [Apache License 2.0](LICENSE.md). Redistributed copies and
derivatives must retain the licence and the [CreeperHost notice](NOTICE). Third-party library
licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
