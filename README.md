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

## Update the existing Paradox Mods listing

The repository includes the official `PublishNewVersion` profile. Publishing is
separate from committing or pushing to GitHub.

1. Restore your original `src/RoadSnow.Mod/Properties/PublishConfiguration.xml`.
   If it is unavailable, copy `PublishConfiguration.example.xml` to that name.
2. Set `ModId` to the existing listing's numeric ID in its **Value attribute**.
   Set `ModVersion` to the next public version and fill in `ChangeLog`.
   Keep the existing description, thumbnail, screenshots, tags, dependencies and
   access level. The example requires a description and a local thumbnail before use.
   `Public`, `Private` and `Unlisted` are case-sensitive. Keep multiline description
   and changelog text at column zero to avoid rendering it as a code block.
3. Ensure the **.NET 6 x64 runtime** is installed (`dotnet --list-runtimes` should
   list `Microsoft.NETCore.App 6.0.x`). A newer SDK/runtime alone does not run the
   installed publisher. Sign into the owning Paradox account in the game once,
   then close the game.
4. Open the solution in Visual Studio. Right-click **RoadSnow.Mod → Publish**,
   select **PublishNewVersion**, inspect the configuration, then click **Publish**.
5. Check the publisher's success message and confirm the new version and changelog
   on the existing Paradox Mods page.

From the repository root, the equivalent command (which **uploads immediately**) is:

```powershell
dotnet publish src/RoadSnow.Mod/RoadSnow.Mod.csproj -c Release /p:PublishProfile=PublishNewVersion
```

Use `PublishNewVersion` to replace the mod's files. `UpdatePublishedConfiguration`
changes listing metadata only; `PublishNewMod` creates a separate listing.
The publisher builds and uploads the complete local mod folder, including
`roadsnow-shaders` and `RoadSnow.Options.mjs`. Keep the existing assembly name.
Publishing configuration and thumbnail are local files excluded from Git.

The official publishing workflow is described in
[Colossal Order's code-modding guide](https://www.paradoxinteractive.com/games/cities-skylines-ii/modding/dev-diary-3-code-modding).
Profile names and metadata fields were checked against the installed 1.6.2f1 toolchain.

## Source layout

`RoadSnow.Core` contains the snow, weather and impression calculations without game
dependencies. In `RoadSnow.Mod`, `Systems` handles the game update loop, `Geometry`
maps network surfaces, `Rendering` manages meshes and texture atlases, `Simulation`
samples lane traffic, and `Settings` handles the options menu and localization.

The `RoadSnowSystem.*.cs` files are parts of the same ECS system. Keep their update
order in `RoadSnowSystem.cs`; settings property names and shader bindings should stay
stable when refactoring. C# formatting follows `.editorconfig` and `.csharpierrc.json`.
