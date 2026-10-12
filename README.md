# StreamZilla

A .NET 8 WPF application with a FileZilla-style dual-pane interface and parallel SFTP downloads, built with MVVM.

## Final tweaks

- **Max streams per file** field is 45 pixels wide, matching the Max concurrent files field.
- The Downloads panel can be resized vertically using the horizontal splitter between the file panels and the queue.
- The Downloads header shows the total downloaded size out of the total size in the queue:

```text
Size: 1.2 GB / 4.8 GB
```

## Queue counters

The Downloads header shows how many files have finished downloading out of the total in the queue:

```text
Downloaded: 3 / 10
```

## Clear queue

**Clear queue** pauses all active downloads first, then removes every entry from the list and deletes its temporary `.streamzilla-download` file.

## Download limits

- **Max streams per file (1-8)** — parallel streams per file; values outside 1-8 show a validation error and are rejected.
- **Max concurrent files (1-8)** — maximum files downloading at once; values outside 1-8 show a validation error and are rejected.

## Remove selected

**Remove selected** removes every download row selected in the Downloads grid.

## Remote selection

Clicking the expander arrow expands or collapses a folder without changing the selection. Plain clicks select one item; Ctrl+click toggles items; Shift+click selects a range.

## Total download speed

The Downloads section header shows the combined speed of all active downloads, before the log path.

## Download completion

When a download finishes, the queue status is updated to Complete and the pause and resume buttons are disabled for that row.

## Live tree updates

- Local expanded folders are watched with FileSystemWatcher.
- Remote expanded directories are polled every 3 seconds while connected.
- Collapsed folders are not polled or watched.

## Scroll into view

When the application loads or connects, the last selected local or remote item is scrolled into view and centered vertically within its TreeView panel.

## Removing downloads

Removing a download before it finishes cancels its active chunk tasks, then deletes the corresponding `.streamzilla-download` temporary file after a short delay.

## SSH host keys

On first connection, StreamZilla captures the server host key through SSH.NET's `HostKeyReceived` event, displays its SHA-256 fingerprint, and asks whether to trust it.

## Download behavior

Downloads are written to a temporary file with the suffix `.streamzilla-download`. The final file is created by renaming the temporary file only after the download completes.

## Architecture

- `Models/` — plain data: settings, remote/local entries, download entries, queue entries
- `ViewModels/` — observable view models and application logic
- `Commands/` — `RelayCommand`
- `Dialogs/` — `IDialogService`, `DialogService`, `InputDialogWindow`, `InputDialogViewModel`
- `Services/` — SFTP, downloads, settings, queue persistence, logging
- `Views/` — WPF window with a thin code-behind

## Storage

```text
%APPDATA%\Roaming\StreamZilla
```

## Requirements

- Windows
- .NET 8 SDK

## Run

```powershell
dotnet restore
dotnet run
```

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

## Security

Settings, accepted SSH host-key fingerprints, and download queues are encrypted with Windows DPAPI for the current Windows user.
