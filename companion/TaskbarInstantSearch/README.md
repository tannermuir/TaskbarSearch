# TaskbarInstantSearch

Companion app for the Windhawk taskbar Search mod.

Expected install path for Windhawk auto-launch:

```text
%APPDATA%\TaskbarInstantSearch\TaskbarInstantSearch.exe
```

Config path:

```text
%APPDATA%\TaskbarInstantSearch\config.json
```

Build with a Windows .NET SDK:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

Copy the published files to `%APPDATA%\TaskbarInstantSearch`.
