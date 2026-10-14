# Snowy Roads

Snow overlays, tire tracks, footprints and snowbanks for Cities: Skylines II.

## Realistic snow

**Appearance → Relief → Realistic mode** adds displaced snow around recorded
impressions and raised snow between worn lane paths. It is experimental and on
by default for new settings (existing saved choices are preserved). Enabling it
in the menu displays a warning about bugs and reduced performance;
it can be disabled again in the same menu. Geometry and nearby detail have bounded
budgets, but the effect still adds rendering work.

Accumulated traffic wear keeps two cleaner wheel corridors with compacted snow
between them, even on busy lanes. Individual tires still deform the snow along
their actual path; motorcycles and bicycles leave one central track. Distant
lane masks preserve the average worn area without increasing texture memory.

## Snowplows and performance

Snowplows clear complete road sections and connected junctions, preserving snow
on sidewalks and raised medians. Cleared sections stay consistent after saving
and loading; fresh snowfall gradually covers them again. Snow pushed aside forms
piles along the road edges.

**Snow → General → Snowplow clearing** controls this effect. Turning it off stops
new clearing and piles; previously cleared sections recover with fresh snowfall.

The update reduces repeated traffic, geometry and texture processing and spreads
expensive work across frames to help busy cities. Improvements depend on the city
and hardware.

City tests showed about 74% lower average mod CPU update time (9.12 ms to 2.41 ms).
These observations span different views and weather conditions; results vary.

A localized **What's new** message explains the changes when the mod opens after
an update. Its saved revision prevents it from appearing again on later launches.

## Build

1. Install the official modding toolchain from **Options → Modding** in Cities:
   Skylines II. Complete its setup, then restart Visual Studio if it was open.
2. Open `RoadSnow.sln` in Visual Studio and select **Release / Any CPU**.
3. Close the game, then choose **Build → Build Solution** (`Ctrl+Shift+B`).

The project uses the official toolchain to resolve game assemblies, run the mod
post-processor and copy the complete package to
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\RoadSnow.Mod`
by default (`CSII_LOCALMODSPATH` defines the actual destination).
The game assemblies are not included in this repository. The post-processor
requires the .NET 6 runtime as well as the SDK used to compile the solution.

The shader bundle is included in `assets/roadsnow-shaders`. A normal solution build
copies it and the RGB options module alongside the DLL; no separate preparation is
needed for a fresh checkout.

## Editing shaders

Ordinary C# changes do not require rebuilding the bundle. When changing shader sources:

1. Create a Unity **2022.3.62f2** project and install HDRP **14.0.12** through Package Manager.
2. Copy `shaders/BuildRoadSnowShader.cs` into `Assets/Editor`.
3. Copy the `.shader`, `.hlsl` and `.compute` files from `shaders` into `Assets/RoadSnow`.
4. Choose **Tools → Snowy Roads → Build Shader Bundle** and select this repository's
   `assets` folder as the output directory.
5. Build the solution again in Visual Studio to include the updated bundle.

The shader builder derives shaders from Unity HDRP. Its license notice is in
[shaders/UNITY-NOTICE.txt](shaders/UNITY-NOTICE.txt).

## Source layout

`RoadSnow.Core` contains the snow, weather and impression calculations without game
dependencies. In `RoadSnow.Mod`, `Systems` handles the game update loop, `Geometry`
maps network surfaces, `Rendering` manages meshes and texture atlases, `Simulation`
samples lane traffic, and `Settings` handles the options menu and localization.

The `RoadSnowSystem.*.cs` files are parts of the same ECS system. Keep their update
order in `RoadSnowSystem.cs`; settings property names and shader bindings should stay
stable when refactoring. C# formatting follows `.editorconfig` and `.csharpierrc.json`.
