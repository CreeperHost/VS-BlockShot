# BlockShot for Vintage Story

Catch something worth sharing? BlockShot sends Vintage Story screenshots straight to
[blocks.hot](https://blocks.hot), without making you leave the game.

## What it does

- Takes screenshots from inside Vintage Story
- Uploads automatically or asks first—your choice
- Copies the share link ready to paste
- Keeps recent uploads together with quick copy, open, and delete controls
- Supports anonymous uploads when you would rather keep your name off them
- Saves a local copy if an upload fails

It is entirely client-side, so the server does not need BlockShot installed.

## Getting started

Drop the ZIP into your Vintage Story `Mods` folder, load a world, then open BlockShot from the
pause menu. Link your MineTogether account in the browser and you are ready to go.

The keys can be changed from Vintage Story's controls menu.

## Building

The project requires the .NET 10 SDK and a Vintage Story installation containing the 1.22.2 or
newer assemblies. Set `VINTAGE_STORY` to the game directory or pass it to the build script:

```powershell
dotnet test tests\BlockShot.VintageStory.Core.Tests\BlockShot.VintageStory.Core.Tests.csproj
.\build.ps1 -VintageStoryPath "C:\Path\To\Vintagestory"
```

The package is written to `artifacts/BlockShot-VintageStory-<version>.zip`.

## License

See [LICENSE.md](LICENSE.md).
