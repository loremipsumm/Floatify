# Privacy Policy

_Last updated: November 2026_

Floatify is a desktop app that shows synced lyrics for the song playing in the Spotify desktop app.

## What Floatify reads
- **The song that is playing** (title, artist, album, length, playback position and album cover), read locally from Windows' media controls. This never leaves your computer, except as described below.

## What Floatify sends
- To look up lyrics, Floatify sends the **song title, artist, album and length** to [LRCLIB](https://lrclib.net), a public lyrics database. LRCLIB's own policies apply to those requests.
- Nothing else is sent. Floatify has no accounts, analytics, telemetry or ads.

## What Floatify stores
Everything stays on your computer, in `%LOCALAPPDATA%\Floatify\`:
- `settings.json`: window position and size, appearance settings and per-song timing offsets
- `lyrics\`: a cache of lyrics already looked up
- `app.log`: a small log for troubleshooting

Deleting that folder removes all of it. Uninstalling the app and removing the folder leaves nothing behind.

## Contact
Questions or concerns: open an issue at https://github.com/loremipsumm/Floatify.
