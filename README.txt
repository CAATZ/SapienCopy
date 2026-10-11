SAPIENCOPY 1.10.0

A portable Windows interface for copying files and folders with Robocopy.
Choose sources, exclusions and a destination, then monitor the transfer without
working at a command prompt.

REQUIREMENTS
64-bit Windows, .NET Framework 4.8 and Windows Robocopy.
Local drive-letter paths; destination formatted as NTFS, exFAT or FAT32.
The Windows system volume must be NTFS for temporary working files.
Extract the portable ZIP and open SapienCopy-v1.10.0.exe. No installation is needed.
The executable is unsigned. SHA-256 checksums accompany the release downloads.

QUICK START
1. Add source folders or individual files, or drag them into the source list.
2. Optionally add folders to Excluded folders.
3. Browse to, type, or drop one folder into Destination folder.
4. Choose a copy mode and a OneDrive policy.
5. Choose whether to calculate percentage/ETA and save logs and reports.
6. Select Start copy. Check the final result and any reported omissions or errors.

COPY MODES
Direct copy places each selection immediately inside the destination.
For example, C:\Projects\Example becomes D:\Copies\Example, and
C:\Settings\settings.json becomes D:\Copies\settings.json.
No dated parent folder or source-drive hierarchy is added. Whole-drive sources
and separate selections with the same final name are rejected in this mode.

New dated copy creates a SapienCopy-<date>-<time>-<id> folder in the destination.
It retains drive-relative source paths: C:\Projects\Example becomes
<new copy folder>\C\Projects\Example.

Update existing copy uses the same drive-relative layout. Choose the previous
copy folder itself as the destination, not its parent.

All modes skip unchanged files, replace differing files and keep files that exist
only at the destination. Existing folders are merged. These are copy operations,
not a mirror: removing or excluding a source does not remove its older copy.

SELECTIONS AND EXCLUSIONS
Individual files are copied by name without adding their siblings. Add files
supports multiple selections. A selected parent folder covers its selected contents.
Each excluded folder excludes that folder and everything beneath it, including
individually selected files. Removing a list entry does not delete any files.

Drop files or folders into Sources, folders into Excluded folders, and exactly one
folder into Destination. Virtual items inside archives are not supported.
If Windows cannot enable drag/drop, use the picker buttons; details appear in
the activity area. Selection controls remain locked during copying and while paused.

"Clear lists after successful copy" clears the source and exclusion selections
after a clean completion. It never deletes source data. Warnings, failures and
cancellation keep the lists populated. The option is off by default.

ONEDRIVE FILES
All files includes online-only content within selected sources. Windows may need
to download it, requiring internet access and free local space.

Downloaded files only uses locally cached directory entries and skips files
marked offline or requiring recall. Cached folders are copied in separate batches,
which can add overhead for many small folders. Names absent from the local cache
cannot be counted; exclusion reports are not a complete cloud inventory.
This is a snapshot of availability. Later file opens can still involve OneDrive
if availability changes, so zero network activity is not guaranteed.

Exclude OneDrive completely skips detected sync roots and selections inside them,
including Desktop or Documents folders redirected into OneDrive.
The app does not change OneDrive synchronization or pinning settings.

PROGRESS AND CONTROLS
"Calculate percentage and ETA (scan first)" is on by default. It estimates the
data that needs copying, including unchanged-file skipping and exclusions.
Uncheck it to skip the extra sizing scan; speed, elapsed time and activity remain.
Compatibility, OneDrive availability and destination checks still run.

Percentage, speed and remaining time are estimates. Buffering, retries, metadata,
source changes and storage features affect them. Speed measures recent Robocopy
writes, not physical drive throughput. One MB means 1,000,000 bytes.
If sizing fails, copying can continue with estimates unavailable; hover over the
percentage label for the reason. Success reaches 100% only when copying finishes.

The Path line shows current activity; hover to read a long path. A scan with no
detectable progress for two minutes stops with its last reported location.
Active transfers and OneDrive downloads have no automatic two-minute timeout.
Threads defaults to 32; a lower value can suit spinning disks or small files.
More threads do not always improve performance.

PAUSE, RESUME AND CANCEL
Pause becomes available after source and destination identities are recorded.
Wait until the app displays Paused. It stops this transfer's copy processes;
it does not eject the drive, stop OneDrive or stop other applications.

To change USB ports: Pause, wait for Paused, use Windows Eject, wait for safe-removal
confirmation, reconnect the drive, then select Resume. Do not disconnect if the
app says it cannot confirm that the worker stopped.

Resume verifies the original source/destination volumes and output folder before
writing. Changed drive letters are handled by volume identity. A missing,
reformatted, locked or mismatched drive blocks Resume and leaves the job paused.
The app never formats a drive, assigns letters or substitutes a different disk.

Completed unchanged files are skipped. Robocopy restartable mode is enabled,
but the interrupted file may start again from the beginning. Exact byte-offset
recovery is not guaranteed, including for USB drives. A dated copy resumes in
its original folder; checks and estimates are rebuilt for the remaining pass.
Elapsed time excludes the pause.

Keep the app open to retain Resume state. It is not saved across app or PC restarts.
Cancel discards that state and keeps copied and partial files. Closing a paused
app asks before discarding the job. Saved reports from a stopped pass may still
say Running or Cancelled; those reports do not establish a completed copy.

DESTINATION FILESYSTEMS AND PROTECTED FILES
NTFS supports the app's Windows link and alternate-stream copying. Links are
preserved as links; their targets are not automatically copied. Select the real
target separately when its contents are needed. Source hard links copy as file data.

exFAT and FAT32 support ordinary files and folders. A compatibility check runs
before copying, even with percentage/ETA disabled. It rejects unsupported links,
reparse points, EFS-encrypted entries and source names differing only by case.
FAT32 also rejects files larger than 4,294,967,295 bytes (4 GiB minus one byte).

On exFAT/FAT32, alternate streams are omitted and reported as metadata warnings.
Sparse and compressed files are copied as ordinary data and can take more space.
Cloud placeholder stream inspection is deferred to avoid fetching content during
preflight; these destinations cannot preserve those streams. FAT32 comparisons
allow two-second timestamp precision.

Include protected files requires Run as administrator and an NTFS destination.
It uses Robocopy /ZB, which can fall back to backup mode after access is denied.
Administrator privileges may also be needed to recreate symbolic links.
Ownership, access-control lists and auditing permissions are not copied.

LOGS AND RESULTS
Save logs and reports is initially off for Direct copy and on for the other modes.
You can change it before starting. When enabled, it saves Robocopy logs,
Summary.csv, Exclusions.csv and STATUS.txt. Direct and Update modes place them in
a new Reports-<date>-<id> folder; dated copies keep them inside the dated folder.
Previously saved reports remain untouched by this option.

When disabled, no logs or reports are added to the destination. Results remain
in the app; there is no saved diagnostic history after closing it. Temporary
local files are cleaned up when the run stops; cleanup problems are reported.
Known omissions are counted in the app and listed in Exclusions.csv when enabled.
A skipped folder represents its entire subtree. A completed copy with exclusions
contains the selected subset, not every file under the original source.

SAFETY AND LIMITS
Existing destination links and selected hard-linked files are rejected to avoid
redirecting writes or changing other linked files. Reports, logs and job files
receive similar checks. Use a fresh destination or resolve the reported conflict.
Source/destination overlaps are rejected, including resolved aliases.
Filesystem changes made after a check can still invalidate its snapshot.

Close applications that change source files when a consistent backup is needed.
Credentials and private files remain sensitive on the destination drive.
Robocopy results are checked, but the app does not hash every copied file or test
restoration. Inspect results before treating the copy as complete.

Physical USB unplug/replug and drive-letter changes, physical exFAT/FAT32 copies,
live OneDrive stalls, elevated Explorer drag/drop and a full backup restoration
have not been validated for this release. Interrupted-file resume remains conditional.

BUILD FROM SOURCE
Extract the optional source ZIP. In Windows PowerShell, run:
  .\build.ps1
The script uses the installed .NET Framework C# compiler; no package restore is needed.
Run the core regression checks with:
  .\test.ps1
Tests create isolated fixtures and keep logs under %LOCALAPPDATA%\SapienCopy\Tests.
Use Get-Help .\test.ps1 -Full for optional test modes and executable checks.

LICENSE
SapienCopy is licensed under the MIT License. Copyright (c) 2026 CAATZ.
See LICENSE.txt for the full terms.

Robocopy option reference:
https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy
