# Discord Launcher (Stream Deck Plugin)

Windows-only Stream Deck plugin with one action: `Launch / Focus Discord`.

## What It Does
- If Discord is not running, launches it via `%LOCALAPPDATA%\Discord\Update.exe --processStart Discord.exe`.
- If Discord is already running, restores and focuses the Discord window (including minimized windows).
- Extracts and uses Discord's real app icon at runtime.
- Shows a Stream Deck-style running badge (fill `#2BAC77`, black ring outline).

## Repo Layout
- `src/com.kenobi.discordlauncher.sdPlugin/manifest.json`
- `src/com.kenobi.discordlauncher.sdPlugin/app.html`
- `src/com.kenobi.discordlauncher.sdPlugin/app.js`
- `src/com.kenobi.discordlauncher.sdPlugin/images/`

## Build / Install
1. Package `src/com.kenobi.discordlauncher.sdPlugin` using Elgato Distribution Tool.
2. Install the generated `.streamDeckPlugin` file.
