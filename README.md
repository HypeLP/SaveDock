# SaveDock

SaveDock finds, backs up, and restores local PC game saves on Windows. The Windows x64 release at `dist\SaveDock.exe` is a single compressed executable with the .NET runtime and offline game catalog built in. The runtime extracts bundled native libraries to a temporary directory when needed.

## Getting started

1. Start SaveDock and choose a backup destination. SaveDock uses a `SaveDock Backups` folder inside that destination.
2. Choose **Backup mode**, then select **Backup** next to a game or **Back up all**.
3. Select **Restore** next to a game, choose a version, and review its destination paths. Close the game before confirming.
4. Select **Undo restore** next to the game to recover the state immediately before the last successful restore. Undo also removes files that the restore created. The local undo snapshot survives an app restart and is consumed after a successful undo.

SaveDock reads local installation data from Steam, Epic Games, GOG, Ubisoft Connect, EA app, Battle.net, and Xbox PC games with a MicrosoftGame.config file. Filter the list by game or launcher. If the catalog is missing a save path, select a game and choose **Add save path**. Use **Add game** to add custom files or folders and optionally choose an icon or a game executable.

To exclude a game from backups, select it and choose **Hide game**. Enable **Show hidden games** to see it again and choose **Unhide game** to resume backups. Existing versions remain available for restore.

## Settings

**Language** switches the interface between English and German. English is the default.

**Automatic** backup mode creates a full ZIP, then up to five differential backups before creating another full ZIP. **Full backup** creates a standalone ZIP. **Differential backup** saves changes relative to the latest full backup and starts with a full backup when required.

**ZIP compression** supports Fast, Compact, and No compression. **Check file contents** is the default change detection mode. The optional metadata check compares file size and timestamps and can miss changes when those values stay the same.

- **Standard history** keeps up to three full backups, each with up to five differential backups.
- **Keep latest version only** retains one standalone ZIP per game.
- **Keep last X versions** limits the restore list. A full ZIP required by a retained differential version may also remain on disk.

Retention applies on the next successful backup of the game. SaveDock stores dated ZIPs and an `index.json` under `SaveDock Backups\SaveDock\<game>-<id>\`. The index records file paths, checksums, and version relationships. Undo snapshots are stored separately under `%LOCALAPPDATA%\SaveDock\Undo`.

Backups from the first prototype with `mapping.yaml` remain readable and keep their original absolute paths. They are outside the new retention rules. For new backups, paths under recognized Windows user folders are resolved for the current user.

## Build

```powershell
dotnet run --project .\SaveDock.csproj
dotnet publish .\SaveDock.csproj -c Release -r win-x64 --self-contained true -o .\dist
```

Publish to an empty folder when replacing an earlier multi-file build so the old DLLs and debug symbols are kept out of the release directory.

The bundled [catalog snapshot](https://github.com/mtkennerly/ludusavi-manifest/tree/ff0faa56840b0081c11a5b2c28f44bd478b29faf) comes from the Ludusavi Manifest. Its license is in `Catalog`. The optional `Catalog/build-catalog.py` script uses PyYAML to rebuild the catalog from a manifest YAML file.
