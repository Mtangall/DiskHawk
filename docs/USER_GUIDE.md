# DiskHawk User Guide

Version 1.4 · [Back to README](../README.md)

DiskHawk shows where disk space is used on Windows machines across your network and lets you clean it up remotely. You add machines to a list, scan them in parallel, see the *hotspot* (the folder where space actually concentrates) for every machine, drill into any machine's folder tree, and delete what is not needed. Every deletion is confirmed by you and recorded in an audit log.

> **Remote deletion is permanent.** Deleted items do not go to the Recycle Bin. Try DiskHawk in a test environment first and keep backups.

## Contents

1. [Requirements](#1-requirements)
2. [Installation](#2-installation)
3. [The main window](#3-the-main-window)
4. [Adding machines](#4-adding-machines)
5. [Scanning](#5-scanning)
6. [Reading a report](#6-reading-a-report)
7. [Cleaning up (deleting)](#7-cleaning-up-deleting)
8. [Reports and exports](#8-reports-and-exports)
9. [Settings](#9-settings)
10. [Command-line scanner](#10-command-line-scanner)
11. [Troubleshooting](#11-troubleshooting)
12. [Files and folders](#12-files-and-folders)
13. [Keyboard shortcuts](#13-keyboard-shortcuts)
14. [FAQ](#14-faq)

---

## 1. Requirements

### Console (the machine you run DiskHawk on)

- Windows 10 / 11 or Windows Server 2016 or later
- .NET Framework 4.8
- An account that is a **local administrator on the target machines** (typically a domain account in a helpdesk/admin group)

### Target machines (the machines you scan)

- Windows 10 / 11 or Windows Server 2016 or later with .NET Framework 4.8
- Reachable from the console:
  - **TCP 135** and the **dynamic RPC range** (TCP 49152–65535 by default) for WMI
  - **TCP 445** (SMB) with the `admin$` share enabled
- Nothing has to be installed on the targets.

> **Local (non-domain) accounts:** Windows filters the administrator token of local accounts over the network (Remote UAC). Use a domain account, or set `LocalAccountTokenFilterPolicy` according to your security policy.

### Endpoint protection

DiskHawk copies a small program, `DiskHawk.Scanner.exe`, to `C:\Windows\DiskHawk\<run>\` on each target and starts it through WMI. Endpoint protection (EDR/antivirus/AppLocker) that blocks unknown programs must allow this file. The current release is not code-signed; allow it by hash or path, or wait for a signed release.

---

## 2. Installation

### Interactive (MSI)

1. Download `DiskHawk-<version>-x64.msi` from the [Releases](../../../releases) page.
2. Run it. The installer shows a welcome page, the license agreement (accept it to continue), the install folder (default `C:\Program Files\DiskHawk`) and asks for administrator approval (UAC).
3. On the last page leave **Launch DiskHawk** ticked to start it right away, or start **DiskHawk** from the Start menu later.

The installer also associates `.dhr` report files with DiskHawk, so you can open a report by double-clicking it. Wherever you install it, only administrators can change the program folder (see [Startup security checks](#startup-security-checks)).

The installer's dialogs and license agreement are in Turkish when the Windows display language is Turkish and in English otherwise. To choose one explicitly, run `msiexec /i DiskHawk-1.4.0-x64.msi ProductLanguage=1033` (English) or `ProductLanguage=1055` (Turkish). This only affects the installer; the application language is set in **Settings**.

### Silent (SCCM, Intune, GPO)

```
msiexec /i DiskHawk-1.4.0-x64.msi /qn
msiexec /x DiskHawk-1.4.0-x64.msi /qn          (uninstall)
```

Installing a newer MSI replaces the older version automatically.

### Portable

Build from source (see the README) and run `DiskHawk.exe` from any folder. Keep `DiskHawk.exe`, `DiskHawk.Core.dll` and `DiskHawk.Scanner.exe` together, and use a folder that **only administrators can write to** (see [Startup security checks](#startup-security-checks)).

### Where your data is stored

| Installation | Data folder |
|---|---|
| MSI (Program Files) | `%LOCALAPPDATA%\DiskHawk` |
| Portable, writable folder | next to `DiskHawk.exe` |
| Portable, read-only folder | `%LOCALAPPDATA%\DiskHawk` |

**Settings → Data folder → Open folder** shows the folder in use. See [Files and folders](#12-files-and-folders) for its contents.

### Startup security checks

At startup DiskHawk checks two things and shows a **Security warning** if something is wrong:

- **The scanner does not match this console.** `DiskHawk.Scanner.exe` is not the file that was built together with `DiskHawk.exe` (it may have been replaced). DiskHawk will not deploy it. Reinstall from a trusted source.
- **Non-administrators can write to the application folder.** Someone could replace the scanner that is sent to every machine. Move DiskHawk to a folder only administrators can write to, such as `C:\Program Files\DiskHawk`.

### Running as administrator

DiskHawk normally runs with your normal rights. That is enough for remote scans, because remote access uses your account's administrator rights on the targets. Use **Run as administrator** when you scan **this computer**: only then can DiskHawk use the fast MFT engine and see every folder.

---

## 3. The main window

From top to bottom:

| Area | What it does |
|---|---|
| **Header** | **Settings** and **About** buttons. |
| **Toolbar** | Machine name box, **Add**, **Import**, **Active Directory** · **Scan selected**, **Scan visible**, **Retry failed**, **Stop** · **This PC**, **Open report**, **Summary CSV**. |
| **Summary cards** | **Machines**, **With report**, **Critical (90%+ full)**, **Warning (80%+ full)**, **Failed**, **Running / queued**. Click a card to filter the list by it; click it again to clear the filter. |
| **Search and filter** | Free-text search across machine names, messages and hotspot paths (Ctrl+F), plus a **Status** filter: All, OK, Failed, Running / queued, Not scanned, 90%+ full, 80%+ full. |
| **Machine list** | One row per machine (see the columns below). Click a column header to sort. |
| **Activity log** | What DiskHawk is doing, with timestamps. Toggle it with the **Activity log** button in the status bar. |
| **Status bar** | Visible / total machines, results of this session, current parallel and timeout settings. |

### List columns

| Column | Meaning |
|---|---|
| Machine | Computer name |
| Status | Queued, a running phase (Ping, Connecting WMI, Copying, Launching, Scanning…, Retrieving report), **OK**, **Failed**, **Cancelled**, or **Previous result** (a report from an earlier session) |
| Drive | The fullest drive of the machine |
| Size / Free / Usage | Size of that drive, free space, and how full it is (as a bar) |
| Hotspot (where the space is concentrated) | The deepest folder that still holds most of the used space, e.g. `C:\Users\ann\AppData\Local\Microsoft\Outlook` |
| Hotspot size | Size of the hotspot folder |
| Largest file | Largest single file found |
| Last scan | Time of the report shown |
| Message | Error text or additional information |

**Right-click** a machine for: **Open result**, **Scan**, **Open C$ share**, **Open report folder**, **Copy machine names**, **Copy message**, **Remove from list**.

---

## 4. Adding machines

You can add machines in four ways:

- **Type a name** in the box and press **Enter** or click **Add**. Several names separated by spaces, commas or semicolons also work.
- **Paste** (Ctrl+V into the box) a multi-line list, e.g. copied from Excel; each line is added.
- **Import** a TXT or CSV file. The first column of every line is used; lines starting with `#` and a header such as `Name` or `ComputerName` are ignored.
- **Active Directory**:
  - **Name filter:** a name pattern such as `NYC-*` (`*` is a wildcard).
  - **OU (optional):** the distinguished name of an OU, e.g. `OU=NewYork,OU=Computers,DC=contoso,DC=local`. A server name or an unescaped `/` is not accepted.
  - **Last active:** only computers that logged on in the last *n* days (0 = all).
  - **Skip disabled computer accounts.**
  - Click **Search**, then **Add to list**.

Names are converted to upper case and duplicates are ignored. Names containing spaces or any of `\ / : * ? " < > | ; ,` are skipped; they are listed in the activity log. IPv4 addresses are accepted.

The list is saved automatically (`machines.txt`). To remove machines, select them and press **Delete** or use **Remove from list**. Reports on disk are kept.

---

## 5. Scanning

### Remote scan

1. Select machines (Ctrl+A selects all) and click **Scan selected** (F5), or click **Scan visible** to scan everything the current search/filter shows.
2. Up to **Parallel scans** machines (default 20) are scanned at the same time; the rest wait as **Queued**.
3. When a machine finishes, its row shows **OK** and the results. Double-click it (or press Enter) to open the report.

**Retry failed** queues every failed or cancelled machine again. **Stop** cancels queued jobs and terminates running scans.

### What happens on a target machine

1. **Ping** — optional reachability check (ICMP, then TCP 445).
2. **Connecting WMI** — encrypted WMI connection with your account.
3. **Copying** — creates a new folder `C:\Windows\DiskHawk\scan_<time>_<id>` that only SYSTEM and Administrators can access, copies the scanner and verifies the copy (SHA-256).
4. **Launching** — starts the scanner hidden, at below-normal priority.
5. **Scanning…** — progress (files, size) is shown in the Status column.
6. **Retrieving report** — copies the report to the console (`Reports\<MACHINE>\`).
7. **Cleanup** — removes the run folder from the target.

### Scan engines

| Engine | When | Notes |
|---|---|---|
| **MFT** | NTFS drive root, scanner running as administrator (always the case remotely) | Reads the NTFS Master File Table directly. Fast (seconds to tens of seconds per drive), counts hard links once, sees every folder. |
| **Classic** | Other file systems, or no administrator rights | Multi-threaded directory walk. Folders it cannot read are marked *No access*. |

The engine used, and for MFT a timing breakdown, is shown in the report header and in the activity log.

### Scanning this computer

**This PC** scans all fixed drives of the console machine. Run DiskHawk **as administrator** to use the MFT engine; otherwise the classic engine is used and some folders may be inaccessible. The result appears in the list under your computer's name.

---

## 6. Reading a report

Double-click a machine (or use **Open report** for a `.dhr` file). The report window shows:

- **Header:** machine, scan time, scanner version, operating system, the account used. If the report has several drives, choose one in the **Drive** box. You also see the drive's total and free space, what was scanned (size, files, folders), the engine, inaccessible folders and cloud-only data.
- **Export CSV** and **Show report file** buttons.

### Folders tab

- **Tree (left):** the folder hierarchy, largest first.
- **List (middle):** the subfolders of the current folder: Name, Size, Share (of the parent folder, with a bar), Files, Subfolders, Last modified, Note. The `[files in this folder]` row stands for the files directly in the folder.
- **Treemap (right):** each rectangle is a folder, sized by its share of the space. Click a rectangle to select it; double-click to go into it.
- **Navigation:** Enter or double-click opens a folder, **Backspace** or **Up** goes one level up, **Root** returns to the drive root.
- **Right-click a folder:** **Open**, **Copy path**, **Open in Explorer** (opens the folder on the remote machine through its `C$` share), **Delete permanently…**

### Largest files tab

The largest files on the drive (by default files of 10 MB and more, up to 1,000 files). Type in **Filter** to search by name or path. Right-click a file: **Go to folder**, **Show in Explorer**, **Copy path**, **Delete permanently…**

### Extensions tab

Space and file count per file extension, with the average size. Double-click an extension (or right-click → **Show largest files with this extension**) to see its largest files.

---

## 7. Cleaning up (deleting)

Deletion happens **on the target machine itself**. DiskHawk sends a list of paths, and the scanner deletes them locally. C$ access is not needed for this.

### Steps

1. In the report, select one or more folders (Folders tab) or files (Largest files tab).
2. Press **Delete** or choose **Delete permanently…**
3. Check the **confirmation dialog**:
   - **Will be deleted:** a normal path.
   - **CRITICAL:** a system or profile path, such as Windows, Program Files, a user profile root, a profile's system folders (Desktop, Documents, Downloads, AppData…), or OneDrive. It is deleted only if you also tick **"… CRITICAL system/profile path(s) too"**.
   - **Never deleted:** a drive root or an invalid path. These items are skipped in every case.
4. Tick **I understand this cannot be undone** and click **Delete permanently (n items, size)**.
5. The **Deletion result** window shows the outcome per item. The report and the list are updated.

Deletion cannot be cancelled once started: a half-finished deletion would leave the machine in an unknown state.

### Result statuses

| Status | Meaning |
|---|---|
| Deleted | Completely removed |
| Partially deleted | Some files could not be removed (open, locked, or no permission); the rest was deleted |
| Failed | Nothing could be deleted |
| Protected | Not touched, because the path is protected, a critical path was not confirmed, or the real location differs (see below) |
| Already gone | The item no longer existed |
| Result unknown | The target did not return a result (timeout, scanner stopped). Rescan the machine to see the current state |

### Built-in safeguards

- **Links are never followed.** If a folder contains a junction or symbolic link, only the link itself is removed, never what it points to.
- **Path rejected: real location differs.** DiskHawk checks where a path really leads before deleting. Paths that go through a junction, a SUBST drive, a volume mounted into a folder, or an 8.3 short name (such as `PROGRA~1`) are refused. Delete those items by their real path.
- **Cloud files.** OneDrive "online-only" files and folders are skipped: deleting them would delete them from the cloud and would not free any local space. Use OneDrive's *Free up space* instead.
- **Open or locked files** are skipped and reported. Close the application that holds them, or delete them after a reboot.
- **The protection check runs twice**: once in the console and again on the target machine.

### Audit log

Every deletion attempt is appended to `deletion_audit.csv` in the data folder (**Show audit log** in the result window). The columns are:

`Time; User; Console; Machine; Type; Path; Expected (MB); Result; Freed (MB); Files deleted; Files not deleted; Details`

---

## 8. Reports and exports

### Report files (.dhr)

Every scan is saved as `Reports\<MACHINE>\<MACHINE>_<time>.dhr` in the data folder. At startup DiskHawk loads the latest report of every machine in the list, shown as **Previous result**. Use **Open report** or double-click a `.dhr` file to open any report.

**Reports opened from a file** (for example one received by e-mail) take the machine name from the file. Before connecting to that machine, whether to open Explorer or to delete, DiskHawk asks for confirmation. Only continue if you trust where the report came from. Corrupt or suspicious reports are refused with a message.

### Summary CSV (fleet)

**Summary CSV** saves one row per drive for all **visible** machines. The row contains:

- machine, status, drive, total, free, usage
- scanned size and file count
- hotspot and its size
- folder with the most files
- largest file
- cloud-only data, inaccessible folders
- last scan time and message

### Report CSV (one machine)

**Export CSV** in a report window writes three files to a folder you choose:

- `<MACHINE>_<time>_folders.csv`: folders to depth 6 that are at least 10 MB
- `<MACHINE>_<time>_files.csv`: largest files
- `<MACHINE>_<time>_extensions.csv`: per-extension statistics

CSV files use `;` as the separator and UTF-8 with BOM, so they open directly in Excel with European regional settings. Cells that would start with `=`, `+`, `-` or `@` get a leading `'`, so file names can never run as Excel formulas.

---

## 9. Settings

Open **Settings** in the header.

| Setting | Default | Description |
|---|---|---|
| Language | English | English or Türkçe; takes effect after restart |
| Parallel scans | 20 | Machines scanned at the same time (1–200) |
| Retries | 1 | Automatic retries on transient errors (0–5) |
| Timeout | 30 | Minutes per machine before a scan is stopped (1–240) |
| Check ping / SMB reachability first | on | Skips offline machines quickly |
| Low disk priority | off | The scanner uses low I/O priority: less impact on the user, slower scan |
| Largest files | 10 MB | Minimum size for the largest-files list |
| Data folder | — | Shows and opens the data folder |

---

## 10. Command-line scanner

`DiskHawk.Scanner.exe` can also be used on its own, for example through SCCM, a GPO startup script or a scheduled task. Run it **as administrator** to use the MFT engine.

```
DiskHawk.Scanner.exe [drive/folder ...] [options]

  (all fixed drives are scanned when no drive is given)
  -o, --out <file|folder>   Report (.dhr) path. Default: MACHINE_timestamp.dhr in the current folder
  -t, --threads <n>         Parallel threads, classic engine
  --min-file-mb <n>         Minimum size for the largest-files list in MB (default 10)
  --max-files <n>           Number of largest files kept in the report (default 1000)
  --csv <folder>            Also write CSV reports
  --engine auto|mft|classic Scan engine (default auto)
  --below-normal            Lower process priority
  --low-io                  Low I/O priority (slower, minimal impact)
  --progress-file <file>    Write progress to a file
  -q, --quiet               No console output
  --delete <list.txt>       PERMANENTLY delete the listed paths instead of scanning
  --delete-log <file>       Deletion result file (TSV)
  --allow-critical          Also delete critical paths (never a drive root)
  --lang en|tr              Language of result texts
```

### Examples

```
DiskHawk.Scanner.exe
DiskHawk.Scanner.exe C: -o \\server\reports$\
DiskHawk.Scanner.exe D:\Data --csv C:\Temp\report --min-file-mb 50
DiskHawk.Scanner.exe --delete C:\Temp\list.txt --delete-log C:\Temp\result.tsv
```

### Deletion list format

The list is a UTF-8 text file with one full path per line, such as `C:\Users\ann\Downloads\old`. If the first line is `#DHLIST1 <count>`, as in lists written by the console, the number of paths must match the count; otherwise nothing is deleted. The same protections apply as in the console.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | OK |
| 2 | Error (details in `<report>.err`) |
| 3 | Cancelled |
| 64 | Invalid arguments |

### Collecting reports on a share

If many machines write reports to one share, allow computer accounts to **create files** only, and keep read and delete permissions for administrators. Open the reports in the console with **Open report**.

---

## 11. Troubleshooting

| Message | Likely cause | What to do |
|---|---|---|
| No response to ping or SMB (445) | Machine is off, not on the network, or 445 is blocked | Check the machine; or turn off **Check ping / SMB reachability first** if only ICMP is blocked |
| RPC server unavailable | WMI ports (135 + dynamic RPC) blocked by a firewall, or the WMI service is not running | Allow *Windows Management Instrumentation (WMI-In)* and RPC in the target firewall |
| WMI connection timed out | Same as above, or a slow link | Check the firewall; raise **Timeout** for slow sites |
| … access denied (run with an administrative account) | Your account is not a local administrator on the target, or Remote UAC filters it | Use a domain account in the target's local Administrators group |
| Scanner exited unexpectedly (possibly blocked by AV/AppLocker) | Endpoint protection stopped `DiskHawk.Scanner.exe` | Allow the scanner in your EDR/AppLocker policy |
| DiskHawk.Scanner.exe is not the version built with this console | The scanner file was changed or replaced | Reinstall DiskHawk from a trusted source |
| Non-administrator accounts can write to the application folder | DiskHawk runs from a folder others can modify | Move it to `C:\Program Files\DiskHawk` (or install the MSI) |
| Timeout (n min) | Very large drive or slow disk | Raise **Timeout**; turn off **Low disk priority** |
| (MFT unavailable: …) | Not an NTFS drive root, or no administrator rights (local scan) | Run DiskHawk as administrator for local scans; the classic engine is used otherwise |
| Could not open report | The file is corrupt, truncated or not a DiskHawk report | Rescan the machine |
| Path rejected: real location differs | The path goes through a link, SUBST drive, mounted folder or short name | Delete the item by its real path |

The **activity log** and `DiskHawk.log` in the data folder contain the details. Unexpected errors are written to `error.log`.

---

## 12. Files and folders

### Data folder

| File | Content |
|---|---|
| `settings.ini` | Settings |
| `machines.txt` | Machine list |
| `Reports\<MACHINE>\*.dhr` | Scan reports |
| `deletion_audit.csv` | Audit log of all deletions |
| `DiskHawk.log` | Activity log (rotated at 10 MB to `DiskHawk.log.1`) |
| `error.log` | Unexpected errors (rotated at 10 MB) |

### On target machines

`C:\Windows\DiskHawk\<run>\` is created for every scan or deletion, accessible only to SYSTEM and Administrators, and removed when the run finishes. Folders older than one day, left behind by interrupted runs, are cleaned up on the next run.

---

## 13. Keyboard shortcuts

| Where | Key | Action |
|---|---|---|
| Main window | F5 | Scan selected machines |
| | Enter | Open result |
| | Delete | Remove from list |
| | Ctrl+A / Ctrl+C | Select all / copy machine names |
| | Ctrl+F | Search |
| | Ctrl+V (in the name box) | Paste a multi-line list |
| Report – folders | Enter | Open folder |
| | Backspace | Up one level |
| | Delete | Delete selected folders… |
| | F5 | Refresh view |
| Report – files | Delete | Delete selected files… |

---

## 14. FAQ

**Why does DiskHawk show less used space than the drive's "used" value?**
Some space is not in files that can be listed: NTFS metadata, the shadow copies of System Restore (*System Volume Information*), and space reserved by the file system. The report header shows both the drive's used space and the scanned size.

**Why is WinSxS smaller in DiskHawk than in Explorer?**
WinSxS contains many hard links to files that also appear in `System32`. The MFT engine counts every file once. Explorer counts it once per name.

**What are "cloud-only" files?**
OneDrive (and similar) placeholders that are listed but not stored locally. They are shown separately and are not counted as used disk space.

**Does DiskHawk leave anything on the target machines?**
No. The run folder is removed after every scan or deletion, and nothing is installed.

**Can I scan machines in another domain or workgroup?**
Yes, if the console can reach the ports listed in [Requirements](#1-requirements) and your account is a local administrator there. Run DiskHawk as that account (e.g. with *Run as different user*).

**Where do I report a bug or a security issue?**
Bugs: GitHub *Issues*. Security vulnerabilities: privately, as described in [SECURITY.md](../SECURITY.md).
