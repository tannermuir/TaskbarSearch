# TaskbarSearch

Windows launcher with an optional Windhawk taskbar integration:

- `TaskbarInstantSearch`, a companion launcher overlay with instant string actions, autocomplete, AI prompting, PDF prefix actions, and window commands.
- Optional: a Windhawk mod that visually moves the Windows taskbar Search box to the left and makes clicking it toggle the launcher.

## Requirements

- Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Optional: [Windhawk](https://windhawk.net/) if you want the taskbar Search box integration

## Install

From PowerShell:

```powershell
.\scripts\install.ps1
```

- publishes the companion application as a self-contained `win-x64` app
- installs it to `%APPDATA%\TaskbarInstantSearch`
- registers it for per-user startup
- starts the app
- creates `%APPDATA%\TaskbarInstantSearch\config.json` from `config.example.json` if no config exists

After install, open the launcher with the default hotkey:

```text
Alt+Space
```

## Optional Windhawk Mod

The application does not require Windhawk. Install `mods/taskbar-search-box-position-v1.wh.cpp` as a Windhawk mod only if you want the Windows taskbar Search box to look and behave like the launch button.

The companion app listens on:

```text
\\.\pipe\TaskbarInstantSearch
```

The Windhawk mod sends a toggle message to that pipe when the taskbar Search box is clicked.

## Configuration

Runtime config lives at:

```text
%APPDATA%\TaskbarInstantSearch\config.json
```

Use `config.example.json` as the portable starting point. It intentionally avoids private paths and secrets.

For AI prompting, set a user environment variable:

```powershell
[Environment]::SetEnvironmentVariable("CEREBRAS_API_KEY", "your-key", "User")
```

Then restart `TaskbarInstantSearch`.

## Build

```powershell
dotnet publish TaskbarSearch.sln -c Release -r win-x64 --self-contained true
```

## Notes

- `bin/` and `obj/` are not committed. Build artifacts are generated locally.
- User-specific bindings, app paths, and secrets belong in local AppData config, not in Git.
