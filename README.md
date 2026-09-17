# Big Walk VR Extras

Small add-on for CircuitLord's **Big Walk VR**. Requires the Big Walk VR mod (it hooks into it at runtime, nothing is replaced).

## Features
- **AUTO SPRINT** row in *Settings > Big Walk VR* (main menu and pause menu). When ON you sprint whenever the left joystick is pushed - no more clicking the left stick.
- **GRIP MODE** row (HOLD / TOGGLE). With TOGGLE you press grip once to grab an item and it stays in your hand; squeeze grip again and the item drops when you let go of the button (swing and let go to throw). Handing an item to your other hand works as before.
- **VIRTUAL CROUCH** row (OFF / HOLD / TOGGLE). HOLD: pull the right joystick back towards you to crouch, let go to stand up. TOGGLE: one pull crouches, the next pull (or a jump) stands you up. Your view is lowered while virtually crouched. Crouching for real still works too.
- **Right joystick click = sit / stand.** Toggles the sitting pose with a short haptic pulse. Physically crouching still sits you as before. (This replaces the VR mod's "recalibrate height" on right-stick click.)

## Install
**Mod manager (recommended):** install with r2modman / Thunderstore Mod Manager like any other Big Walk mod, then launch with *Start modded*.

**Manual:** drop `BigWalkVRExtras.dll` into `Big Walk\BepInEx\plugins\` (any subfolder is fine) of an install that already has Big Walk VR.

## Config
`BepInEx/config/blowntobytes.bigwalkvr.extras.cfg` (created on first launch):
- `AutoSprint`, `GripToggle`, `VirtualCrouchMode` (0 off / 1 hold / 2 toggle) - same as the in-game rows. `VirtualCrouchThreshold` (0.6) is how far back the stick has to go; `VirtualCrouchCameraDrop` (0.45 m) is how much the view is lowered.
- `RightStickClickSit` (true) - set false to get the original recalibrate-height click back.
- `HoldRightStickToRecalibrate` (false) - short click = sit/stand, holding the click for `HoldSeconds` (0.5) recalibrates height, so you keep both.

## Notes
Built against Big Walk VR 1.0.18. If a future VR mod update changes its internals the plugin will log an error instead of doing anything; just remove it until it is updated.

## Building
`src/Plugin.cs` is the whole plugin. It is compiled with `csc` (see `src/build.sh`) against the game's `BepInEx/core` and `BepInEx/interop` assemblies, the `dotnet/` runtime that ships with the BepInEx pack, and a publicized copy of `BigWalkVR.dll` (the `IgnoresAccessChecksTo("BigWalkVR")` attribute lets the runtime allow that). `thunderstore/` holds the Thunderstore package metadata.
