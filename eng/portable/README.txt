Max Planner - portable edition
==============================

Max Planner is an offline planner for Change Requests, tasks, Subtask cards, checklists and reviews. This folder is
the whole application: nothing is installed, and no administrator rights, network, .NET or Windows App Runtime
installation, or Office are needed.

Requirements
------------
- Windows 11 x64. Tested on 24H2 (build 26100) and 25H2 (build 26200). Builds from 22H2 (22621) are expected to work
  but are not tested.
- A folder you can write to (for example under Documents or on a USB drive). Do not run it from Program Files, a
  network share, or a folder that a cloud service synchronizes while the app is open.

Start
-----
1. Extract the ZIP. Keep the MaxPlanner folder together; do not move single files out of it.
2. Run MaxPlanner.exe.
3. On the first start the app creates a "data" folder beside MaxPlanner.exe. All your records, settings, backups and
   logs live there and nowhere else.

Starting it a second time from the same folder brings the open window to the front instead of opening a second copy.

Your data
---------
- data\data.db holds your records (SQLite). data\backups\ holds a daily backup (the last 7 are kept), safety backups
  made before restores, imports and upgrades, and any backup you make in Settings > Data.
- To move or copy Max Planner, close it first, then move or copy the whole MaxPlanner folder (including data).
- To back up by hand, close the app and copy the data folder, or use Settings > Data > Back up now.

Updating to a new version
-------------------------
1. Close Max Planner.
2. Make a copy of the data folder (recommended).
3. Extract the new ZIP over the old MaxPlanner folder, or extract it elsewhere and copy your data folder into the new
   MaxPlanner folder. The ZIP never contains a data folder, so extracting it cannot overwrite your records.
4. Start MaxPlanner.exe. If the new version needs to upgrade the data, it makes a safety backup first.

An older version refuses data that a newer version has upgraded; keep using the newer version, or restore a backup
made before the upgrade.

Removing it
-----------
Close the app and delete the MaxPlanner folder. Keep the data folder if you may need your records later.

If it does not start
--------------------
- "Max Planner can't save data in this folder": the folder is read-only or protected. Move the whole MaxPlanner
  folder to a folder you can write to (for example Documents) and start it again.
- "This Max Planner folder is already open in another window": close it there (or wait until a closing window has
  finished), then start again.
- "Recovery" page: the data is missing, damaged, or from a newer version. The page explains the choices; nothing is
  replaced without your confirmation.
- Logs are in data\logs (they contain no record titles or notes).

Verifying the download
----------------------
SHA256SUMS.txt lists the SHA-256 checksum of every file in this folder. In PowerShell:
    Get-FileHash .\MaxPlanner.exe -Algorithm SHA256
The ZIP's own checksum is published next to it (MaxPlanner-<version>-win-x64.zip.sha256).

Privacy
-------
Max Planner does not use the network: no account, activation, telemetry, update check or online license check.

Licenses
--------
THIRD-PARTY-NOTICES.txt lists the components included with Max Planner and their licenses.
