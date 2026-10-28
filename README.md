<p align="center">
  <img src="src/Faderly/Assets/Faderly.png" width="96" alt="Faderly icon" />
</p>

<h1 align="center">Faderly</h1>
<p align="center">A clean, modern volume mixer for Windows.</p>

<p align="center">
  <a href="https://github.com/larosa023/Faderly/releases/latest/download/Faderly-win-Setup.exe"><b>⬇ Download Faderly for Windows</b></a>
</p>

---

## Features

- **Per-app volume.** Every app playing audio gets its own fader, mute button and live level meter. Apps with several audio streams (browsers, games) are grouped into one row.
- **Master volume and output switching.** Change the default playback device right from the mixer.
- **Lives in the tray.** Click the tray icon or press **Ctrl + Alt + M** to pop it open; click away or press **Esc** to hide it.
- **Scroll to adjust.** Mouse wheel over any fader moves it 2% (hold Shift for 10%).
- **Light and dark.** Follows your Windows app theme.
- **Updates itself.** Faderly checks GitHub Releases in the background, downloads new versions, and shows **Restart to update** when one is ready. You can also check manually in Settings.
- **Start with Windows** (on by default, toggle in Settings or the tray menu).

## Install

Download **Faderly-win-Setup.exe** from the [latest release](https://github.com/larosa023/Faderly/releases/latest) and run it. It installs per-user (no admin needed) and adds a Start menu shortcut.

> Faderly isn't code-signed yet, so Windows SmartScreen may show "Windows protected your PC". Click **More info → Run anyway**.

Uninstall from **Settings → Apps → Installed apps**.

## Releasing a new version

Releases are fully automated by GitHub Actions ([`release.yml`](.github/workflows/release.yml)):

```bash
git tag v0.2.0
git push origin v0.2.0
```

The workflow builds a self-contained `win-x64` app, packages it with [Velopack](https://velopack.io), and publishes a GitHub Release containing:

- `Faderly-win-Setup.exe`: the installer
- `Faderly-win-Portable.zip`: portable build
- `.nupkg` and `releases.win.json`: what the built-in updater downloads (full and delta packages)

Installed copies pick up the new version automatically within a few hours, or immediately via **Settings → Check now**.

## Building locally

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows.

```bash
dotnet run --project src/Faderly
```

The updater only runs in installed builds; when run from source, Settings shows "Updates work in the installed version."

## Tech

- .NET 8 + WPF
- [NAudio](https://github.com/naudio/NAudio) for Windows Core Audio (sessions, endpoint volume, peak meters)
- [Velopack](https://velopack.io) for the installer and delta auto-updates

## License

[MIT](LICENSE)
