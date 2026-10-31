# Max Planner — deployment guide

This guide covers the portable release: what the artifact contains, how it is built and checked, how users install, move, upgrade and remove it, and how the release gates of T-11 are run on a clean machine. Developer tooling is in [dev-prerequisites.md](dev-prerequisites.md); end-user instructions also ship inside the ZIP as `README.txt` (source: `eng/portable/README.txt`).

Status: the scripts below are written; the release gates have **not been run yet**. Their evidence goes to `docs/evidence/phase-18/` and `docs/evidence/phase-17/`.

## 1. End-user prerequisites (T-02, T-03)
| Item | Requirement |
|---|---|
| Operating system | Windows 11 x64. Tested matrix: 24H2 (26100) and 25H2 (26200). Compatibility floor 22H2 (22621), untested-compatible (ADR 0019). |
| Installed software | None. No .NET, no Windows App Runtime, no Office, no database server, no Store access, no package registration. |
| Rights | Standard user. No administrator rights. |
| Network | None. The app never uses the network (no telemetry, activation, update or license check). |
| Storage | A folder the user can write to. Live use on network shares or folders a cloud client synchronizes while the app runs is unsupported (T-03.7). |

## 2. The artifact
`scripts/publish-portable.ps1 -Version <x.y.z>` produces:

```text
artifacts/
  MaxPlanner-<version>-win-x64.zip          # root folder MaxPlanner/
  MaxPlanner-<version>-win-x64.zip.sha256   # checksum of the ZIP
MaxPlanner/
  MaxPlanner.exe                            # unpackaged, self-contained (ADR 0003)
  ...                                       # .NET 10 runtime, Windows App SDK 2.x runtime, WinUI, e_sqlite3.dll, resources
  THIRD-PARTY-NOTICES.txt                   # every package in the payload and its license (T-03.9)
  README.txt                                # end-user instructions
  SHA256SUMS.txt                            # SHA-256 of every other file in the folder
                                            # no data/ folder: it is created beside the EXE on first start (T-03.8)
```

The publish script fails when:
- a required file is missing (`MaxPlanner.exe`, `MaxPlanner.dll`, `MaxPlanner.pri`, `Microsoft.ui.xaml.dll`, `hostfxr.dll`, `e_sqlite3.dll`);
- the output contains a `data/` folder;
- a development file reaches the payload (fixture generator, benchmarks, BenchmarkDotNet, test assemblies);
- `THIRD-PARTY-NOTICES.txt` does not list exactly the packages of the published `MaxPlanner.deps.json` (plus the Windows App SDK Base package).

Trimming, Native AOT, single-file and ReadyToRun stay off (T-03, `Directory.Build.targets` guard). The binaries are **not signed**: no signing certificate is available, and signing is not an end-user prerequisite (T-03.9). Windows may show SmartScreen for a downloaded, unsigned EXE; users can unblock the ZIP (Properties › Unblock) before extracting.

## 3. Building a release
```powershell
./scripts/build.ps1 -Locked                  # restore from lock files; 0 warnings required
./scripts/test.ps1 -IncludeSmoke             # all tests, including the Desktop smoke tests that launch the app
./scripts/publish-portable.ps1 -Version 1.0.0
```
After a dependency change (a new package or version), the first publish fails the notices check. Regenerate and publish again:
```powershell
./tools/Generate-Notices.ps1                 # reads artifacts/publish/MaxPlanner/MaxPlanner.deps.json and the NuGet cache
./scripts/publish-portable.ps1 -Version 1.0.0
```
`Generate-Notices.ps1` takes each package's license from its nuspec: the package's own license file, or the SPDX template in `tools/notices/` (MIT, Apache-2.0, BSD-2-Clause) with the package's copyright line, plus any notice file the package ships. A new SPDX expression needs a new template file. A second publish must leave every `packages.lock.json` unchanged.

## 4. Installing, moving, upgrading, removing
| Task | Steps | Guarantees |
|---|---|---|
| First start | Extract the ZIP to a writable folder; run `MaxPlanner.exe`. | `data/` is created beside the EXE with `data.db`, `configuration.json`, `backups/`, `logs/`, `temp/` and `.lock`. Paths resolve from `AppContext.BaseDirectory`, never the working directory (T-03.4). |
| Second start | Run the EXE again from the same folder. | The running window comes to the front; the second process exits with 0 (ADR 0013). A different copy of the app (another folder) runs independently. |
| Move / copy | Close the app, then move or copy the whole `MaxPlanner` folder (any drive, path with spaces or Unicode). | Records, links, preferences and archives stay with the folder (AC-02). |
| Back up | Settings › Data › Back up now, or close the app and copy `data/`. | Daily automatic backups (7 kept), safety backups before restore, import into a non-empty dataset, and migration; manual backups are never pruned (T-09, ADR 0015). |
| Upgrade | Close the app; copy `data/` (recommended); extract the new ZIP over the old folder, or extract elsewhere and copy `data/` in. | The ZIP has no `data/`, so extraction cannot overwrite records. A schema upgrade makes a safety backup first. |
| Roll back | Use the previous ZIP with a `data/` backup from before the upgrade. | An older build refuses a newer schema (recovery page, exit code 4) instead of opening it (T-03.8). |
| Remove | Close the app and delete the folder. | Nothing else is written by the app: no registry, AppData, or package registration (T-03.5). Windows-managed caches are outside this guarantee. |

### Exit codes (ADR 0003)
| Code | Meaning | What the user sees |
|---|---|---|
| 0 | Normal exit, or redirected to the running instance | — |
| 2 | `data/` is not writable | "Max Planner can't save data in this folder: …" with how to move the folder |
| 3 | The dataset is locked by another process | "This Max Planner folder is already open in another window…" |
| 4 | Closed from the recovery page for a newer schema | Recovery page explaining the newer version |
| 5 | Startup failure (closed from the recovery or failure page) | Recovery or failure page with the reason |

## 5. Release gates (T-11.6, AC-01, AC-02)
| Script | What it proves |
|---|---|
| `scripts/verify-portable.ps1 -Zip <zip> [-PreviousZip <older zip>] [-SecondDrive E:\]` | **Launch**: responsive window, `data/` with `.lock` and `data.db` beside the EXE, "Startup ready", exit 0. **Checksums**: ZIP vs `.sha256`; every file vs `SHA256SUMS.txt`; notices and README present; no `data/` in the ZIP. **Relocation**: a folder moved (optionally to another drive) reopens the same dataset with `data.db` unchanged, no first-run creation, no recovery. **SecondLaunch**: redirect exits 0, one instance left. **ReadOnlyFolder**: denied write on `data/` → message, exit 2, no database created. **LockedDataset**: `.lock` held → message, exit 3. **NewerSchema**: `user_version` raised by the fixture tool → recovery page, exit 4. **Upgrade**: extracting the ZIP over a used folder leaves `data/` byte-identical and the new build opens it. **DependencyAbsence**: no new MaxPlanner AppData folders, HKCU\Software keys, or packages; no new Windows App Runtime package; `coreclr.dll`, `Microsoft.ui.xaml.dll`, `Microsoft.WindowsAppRuntime.dll` and `e_sqlite3.dll` load from the app folder. Writes `verify-portable.json`. |
| `scripts/run-performance.ps1 [-Zip <zip>] [-Scale 1.0]` | Generates the T-11 fixture, runs the BenchmarkDotNet suite (snapshot load, store initial load, save-to-publication, Dashboard recompute, filter apply, export/import), and launches the published EXE on the fixture: startup to the first rendered ready view on every launch ≤ 3 s, page-switch p95 ≤ 250 ms over Ctrl+1…5 cycles, idle working set ≤ 500 MiB, live store subscriptions not growing. Writes `performance-results.json` and the BenchmarkDotNet reports. |

Both scripts work only on disposable folders they create, and remove them unless `-KeepWork` is passed.

## 6. Clean machine verification (T-11.6)
A developer-machine run of these gates is local evidence only. The release proof is a clean, **offline** Windows 11 machine with no developer tools, no separately installed .NET or Windows App Runtime, and no Office, on both tested builds (24H2 and 25H2).

### 6.1 Interim: Windows Sandbox
Windows Sandbox starts from a clean image of the host's Windows build and is discarded on close. It is an interim check (one build, the host's), not a substitute for the VM gate.
1. Copy the ZIP and its `.sha256` into a host folder, for example `C:\MaxPlannerGate\in`.
2. Start the sandbox with networking off and the folder mapped read-only, for example with this `MaxPlanner-offline.wsb`:
   ```xml
   <Configuration>
     <Networking>Disable</Networking>
     <MappedFolders>
       <MappedFolder>
         <HostFolder>C:\MaxPlannerGate\in</HostFolder>
         <SandboxFolder>C:\Gate</SandboxFolder>
         <ReadOnly>true</ReadOnly>
       </MappedFolder>
     </MappedFolders>
   </Configuration>
   ```
3. In the sandbox, copy the ZIP to `C:\Users\WDAGUtilityAccount\Desktop\Max Planner Tëst\`, extract it, and run `MaxPlanner.exe`: record a screenshot of the window, create a task, close, start again and confirm the task is there (AC-01).
4. The WinApp CLI route (`winappcli:winapp-sandbox` skill, `winapp ... --on sandbox`) can drive the same steps and capture evidence; it uses WinApp's own managed sandbox, which has networking unless configured otherwise. Record which route was used.

### 6.2 Release: clean offline VM
1. Create Hyper-V (or equivalent) VMs from Windows 11 24H2 and 25H2 x64 evaluation media. Install no developer tools, .NET, Windows App Runtime, Visual C++ redistributables or Office. Create a **standard** (non-administrator) user.
2. Disconnect the network adapter. Copy in the ZIP, its `.sha256`, and this repository's `scripts/verify-portable.ps1` on removable or mapped media.
3. As the standard user, manually: extract to a path with spaces and Unicode; start the app; confirm the window, theme and Mica/solid backdrop; create a task; close; restart; confirm the task persisted (AC-01). Move the folder to another drive letter and repeat (AC-02). Record screenshots.
4. Then run the scripted gates. `verify-portable.ps1` needs PowerShell 7; use the portable PowerShell 7 ZIP (no installation) only after the manual launch above, so the first launch is proven without it:
   ```powershell
   pwsh -File .\verify-portable.ps1 -Zip .\MaxPlanner-1.0.0-win-x64.zip -Gates Launch,Checksums,Relocation,SecondLaunch,ReadOnlyFolder,LockedDataset,Upgrade,DependencyAbsence -SecondDrive E:\ -EvidenceDir .\evidence
   ```
   The **NewerSchema** gate needs the fixture generator (and so the .NET SDK); run it on the development machine or in the sandbox instead.
5. Repeat with a second user account (T-11.6 "another account").
6. Copy `evidence/` back and store it under `docs/evidence/phase-18/<date>/` with the VM builds, the user type, and any failed or skipped check stated plainly.
7. If the app fails to start only on the clean VM, check for a missing native dependency (Visual C++ runtime, ADR 0003 open point) before anything else: `WinRT`/`coreclr` load failures appear in the Windows Application event log.

## 7. Relation to other channels
MSIX, Store and code-signing workflows are out of scope for release 1; the portable ZIP is the only required artifact (T-03.3, ADR 0003). The `winui:winui-packaging` guidance was used only for self-contained deployment settings.
