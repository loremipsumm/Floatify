# Floatify

A floating lyrics window for the **Spotify desktop app on Windows**. Minimize Spotify and the lyrics stay on top of your other apps, in sync with the song that's playing.

> Floatify is an independent project. It is not affiliated with, endorsed by or sponsored by Spotify AB. "Spotify" is a trademark of Spotify AB.

```
 Song Title  —  Artist
        previous line
     LINE BEING SUNG NOW
          next line
```

## How it works

| Part | Source | Notes |
|---|---|---|
| Track + playback position | **Windows Media Control (SMTC)**, the same source as the Windows media flyout | No login, no API key, no Premium needed |
| Lyrics | **[LRCLIB](https://lrclib.net)**, an open database of time-stamped (LRC) lyrics | Free, no API key |
| Display | WPF (.NET 10) styled after Apple Music: liquid glass (frosted desktop blur, sheen, specular rim) over an album-art backdrop, left-aligned bold lyrics with dimmed and blurred inactive lines, spring motion | Always on top, hidden from the taskbar and Alt+Tab, never steals focus |

Spotify only reports its playback position every 4–5 seconds, so the position in between is extrapolated (`PlaybackClock`). Lyrics are fetched at runtime and cached locally; no lyrics are bundled with the app.

## Running

**Requirements:** Windows 10 (2004+) or 11, and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (included with the .NET 10 SDK).

- **Just use it:** run `publish\Floatify.exe`.
- **Rebuild the `.exe`:** run `publish.cmd`.
- **Development:** `dotnet run --project src\LyricsFloat.App`
- **Tests:** `dotnet test`

## Everyday use

- **Starts with Windows** (optional, offered on first run): Floatify launches quietly in the background and stays hidden until Spotify starts playing.
- **Shows and hides itself**: the overlay appears when a song plays and slips away when Spotify closes or stays paused for a minute. Turn this off with *Hide when Spotify isn't playing* in the ⚙ panel or the tray menu.
- If you hide it yourself (✕, `Ctrl+Alt+K` or the tray), it stays hidden until you bring it back.
- A short welcome card explains the basics the first time you open it.

## Usage

- **Move** the window by clicking and dragging its middle.
- **Resize** by dragging any edge or corner. Resizing never changes the text size; the width sets how long a line can be.
- **Hover** over the window to show its controls (they slide in one after another):
  - ⏮ ⏯ ⏭ : previous / play-pause / next track in Spotify
  - `−` / `+` : shift the lyrics timing by ±0.25 s, or ±2 s with **Shift** (remembered per song; the badge shows the offset, right-click it to reset)
  - ⚙ : **Appearance** panel with *Transparency* (solid → clear liquid glass) and *Text size* sliders
  - 🔒 : **lock & click-through**. The window stops taking clicks and passes them to whatever is behind it
  - ✕ : hide the window
- **Unlocking:** hover over the lyrics and click the **Unlock** button that appears at the top right. `Ctrl+Alt+L` and the tray menu work too.
- Lyrics **scroll smoothly**: the next line rises and grows into the current one. Track changes and seeks crossfade instead.
- **Click any line** to jump Spotify to that lyric (lines highlight on hover).
- **Scroll** with the mouse wheel to browse earlier or later lines. Following pauses while you browse; click **Sync** (or wait a few seconds) to return to the line being sung.
- **Lyrics out of sync?** Right-click a line just as it's sung: Floatify works out the offset and saves it for that song.
- The header shows the album cover, and Apple Music-style now-playing bars bounce while music plays. The scrubber shows elapsed and remaining time.
- **Tray icon** (purple ♪): show/hide, lock, transparency presets (Solid/Normal/Frosted/Clear), reset position, start with Windows, and quit.

### Reconnecting after connection problems

When lyrics can't be loaded because the internet is down or the LRCLIB server is having trouble, the app keeps trying:

- **Retries automatically** for as long as the same song is playing, waiting 3 → 5 → 10 → 20 → 30 seconds between attempts (then every 30 seconds). If the server asks for a longer wait (`Retry-After`), it is honored, up to 5 minutes.
- **Reconnects right away** when the Windows network changes, for example when Wi-Fi comes back.
- The window shows the cause (**"No internet connection"** or **"Lyrics server is busy"**), a countdown, and a **Try now** button.
- While the window is locked, use the **Reconnect now** tray menu item.

Connection failures are never cached, so a song that failed is looked up again the next time it plays.

| Shortcut | Action |
|---|---|
| `Ctrl+Alt+L` | Toggle lock & click-through |
| `Ctrl+Alt+K` | Show/hide the window |

## Data

Everything is stored in `%LOCALAPPDATA%\Floatify\`:

- `settings.json`: position, size, transparency, text size, lock state, auto-hide, per-song timing offsets
- `lyrics\`: lyrics cache (songs with no lyrics found are looked up again after a day)
- `app.log`: log for troubleshooting

## Limitations

- Only reads the Spotify **desktop** app on this PC, not Spotify playing on a phone or in the web player.
- Not every song is on LRCLIB. When only untimed lyrics exist, the lines are spread evenly across the song and the header shows "unsynced".
- Spotify Free ads are detected heuristically (an empty artist, or "Spotify" as the artist).
- The overlay can't appear on top of games in *exclusive fullscreen* (borderless and windowed modes are fine).

## Project structure

```
src/LyricsFloat.Core/     pure logic: LRC parser, timeline, clock, LRCLIB client, cache, retry policy
src/LyricsFloat.App/      WPF app: window, SMTC watcher, tray, hotkeys, settings
tests/LyricsFloat.Core.Tests/
tools/SmtcProbe/          diagnostic tool that prints Spotify's SMTC data  (dotnet run --project tools\SmtcProbe -- 10)
```

## License and privacy

- [MIT License](LICENSE)
- [Privacy policy](PRIVACY.md): Floatify sends only the song's title, artist, album and length to LRCLIB to find lyrics; nothing else leaves your computer.
