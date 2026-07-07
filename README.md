# HoianViewer

Standalone(as in, a separate exe, not part of the game) Splatoon 3 player viewer (and other models viewer), aiming to replicate Splatoon 3's PlayerCustomPart/Mgr stuff and rendering. You can see (almost exactly - there may be bugs, and hair collision is not 100% exact) how your player mods look like in game, or in general make promo-like player renders.

## Building

Requires .NET 6 SDK.

```
cd PlayerViewer
dotnet build -c Release
```

Which builds to  `PlayerViewer/bin/Release/net6.0/PlayerViewer.exe`.

## Setup

On first launch, the viewer asks for a romfs path. Point it at a Splatoon 3 romfs dump of the version you are using (you can dump it with Ryujinx -> Right Click Splatoon 3 -> Dump -> Romfs). The path is saved to `config.json` next to the exe, you can change it there or in File dropdown in the app if you want to change the version.

Shader archives (`.bfsha`) are read from `romfs/ShaderData/` at runtime. The first launch compiles and caches all referenced shader programs, which may lag a little, subsequent launches reuse the cache.

## Layered filesystem

The romfs loader supports romfs mods. If you have a mod that overrides files (atmosphere-style romfs directory), put the override files in the same directory structure alongside the base romfs. The loader checks the layered path first, then falls back to the base dump.

## Drag and drop

You can drag `.bfres` or `.bfres.zs` files onto the viewer window to open them as standalone models. Animations embedded in the file show up in a dropdown.

## What it does

**Player mode**: Recreation of the PlayerViewer functionality. Note that the Hair Physics, while directly using the havok cloth data, are not necessarily 100% accurately simulated, since I didn't make a proper decomp of havok clothes (but they are directly using the havok cloth data, and are mostly accurate)

**Standalone mode**: you can also load any BFRES model. Skeletal animations will be listed, and playing them will play their corresponding material, texture pattern, and visibility animations. Individual meshes can be toggled on/off.

**Recording**: captures the viewport to an mp4 via ffmpeg (must be on PATH or in same folder as the app). 

**Environment**: switch between the Viewer lighting and the AutoWalk stage lighting. You can also toggle Shadow Prepass (the models casting shadows)

## Project layout

```
PlayerViewer/          the viewer app
Cafe-Shader-Studio/    rendering engine
ShaderLibrary/         shader binary parser
```

## Credits

The viewer itself, Splatoon 3 Renderer & various fixes by [nvnprogram](https://github.com/nvnprogram).

Original versions of Cafe Shader Studio and ShaderLibrary by [KillzXGaming](https://github.com/killzxgaming).

Initial reference for loading the bphcl file by [RAMDRAGONS](https://github.com/RAMDRAGONS)

General assistance with models and stuff [OctoSquiddy](https://github.com/OctoSquiddy)