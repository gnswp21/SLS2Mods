# Patch Notes - 2026-05-01

## Public Beta Compatibility Patch

This patch addresses build errors and compatibility issues with the Slay the Spire 2 Public Beta (Godot 4.5.1 / .NET 9.0).

### Fixed Build Errors
- **UndoAndRedo**: Commented out the call to `MegaCrit.Sts2.Core.Hooks.Hook.BeforePlayPhaseStart` in `UndoAndRedoMod.cs`.
  - **Reason**: This hook was removed or renamed in the latest game assemblies, causing a compilation failure.
  - **Impact**: This was part of a "safety net" deferred check. Standard undo/redo functionality remains intact.

### Installation & Build Notes
- **.NET SDK**: Requires .NET 9.0 SDK.
- **Dependency**: Reference `sts2.dll`, `0Harmony.dll`, and `GodotSharp.dll` from the `<game>/data_sts2_windows_x86_64/` directory.
- **Required Files**: For the mod to load correctly, three files per mod must be present in the `mods/` directory:
  - `ModName.dll` (Compiled binary)
  - `ModName.json` (Manifest)
  - `ModName.pck` (Godot resource pack - can be a dummy file if no custom assets are used).

### Known Issues to Monitor
- **Double-triggering on Turn End**: Investigating reports where turn-end effects might trigger twice after an undo action. Please monitor logs for duplicate `StartTurn` or `DelayedPlayPhaseCheck` entries.
