# Spotify Lyrics Float

Jendela lirik mengambang untuk **aplikasi Spotify desktop di Windows**. Spotify boleh di-minimize; liriknya tetap tampil di atas aplikasi lain, tersinkron dengan lagu yang sedang diputar.

```
 Judul Lagu  —  Artis
        baris sebelumnya
   BARIS YANG SEDANG DINYANYIKAN
        baris berikutnya
```

## Cara kerja

| Bagian | Sumber | Catatan |
|---|---|---|
| Lagu + posisi detik | **Windows Media Control (SMTC)**, sama dengan media flyout di Windows | Tanpa login, tanpa API key, tanpa Premium |
| Lirik | **[LRCLIB](https://lrclib.net)**, database lirik terbuka dengan timestamp (LRC) | Gratis, tanpa API key |
| Tampilan | WPF (.NET 10), tema *liquid glass*: blur asli dari latar belakang (DWM), tint, kilau dan pinggiran mengkilap | Selalu di atas, tidak muncul di taskbar/Alt+Tab, tidak mencuri fokus |

Spotify hanya mengirim posisi lagu sekitar tiap 4–5 detik, jadi posisi di antaranya dihitung sendiri (`PlaybackClock`). Lirik diambil saat runtime lalu di-cache di komputer, jadi tidak ada lirik yang dibundel di dalam aplikasi.

## Menjalankan

**Kebutuhan:** Windows 10 (2004+) / 11, [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (sudah termasuk kalau .NET 10 SDK terpasang).

- **Pakai saja:** jalankan `publish\SpotifyLyricsFloat.exe`.
- **Build ulang `.exe`:** jalankan `publish.cmd`.
- **Mode development:** `dotnet run --project src\LyricsFloat.App`
- **Test:** `dotnet test`

## Pemakaian

- **Geser** jendela dengan klik-tahan di bagian tengah.
- **Resize** dengan menarik sisi atau sudut mana saja. Tinggi jendela menentukan ukuran teks, lebarnya menentukan panjang baris.
- **Arahkan mouse** ke jendela untuk memunculkan tombol:
  - `−` / `+` : geser sinkron lirik ±0,25 detik (disimpan per lagu)
  - 🔒 : **kunci & tembus klik**. Jendela tidak bisa diklik lagi, jadi klik diteruskan ke aplikasi di belakangnya
  - ✕ : sembunyikan
- **Membuka kunci:** arahkan kursor ke lirik, lalu klik tombol **Buka kunci** yang muncul di kanan atas. Bisa juga lewat `Ctrl+Alt+L` atau menu tray.
- Lirik **bergulir halus**: baris berikutnya naik dan membesar jadi baris aktif. Saat ganti lagu atau seek, lirik berganti dengan fade.
- **Ikon tray** (♪ ungu): tampilkan/sembunyikan, kunci, warna kaca (Bening/Tipis/Sedang/Gelap), reset posisi, jalankan saat Windows mulai, keluar.

### Reconnect saat koneksi bermasalah

Kalau lirik gagal dimuat karena internet terputus atau server LRCLIB sedang error, aplikasi tidak menyerah:

- **Mencoba lagi otomatis** selama lagunya masih sama, dengan jeda 3 → 5 → 10 → 20 → 30 detik (lalu tiap 30 detik). Kalau server meminta menunggu (`Retry-After`), permintaan itu diikuti, maksimal 5 menit.
- **Langsung tersambung lagi** begitu jaringan Windows berubah, misalnya WiFi tersambung kembali.
- Jendela menampilkan penyebabnya (**"Koneksi internet terputus"** atau **"Server lirik sedang sibuk"**), hitungan mundur, dan tombol **Coba sekarang**.
- Saat jendela terkunci, gunakan menu tray **Hubungkan ulang sekarang**.

Kegagalan koneksi tidak disimpan ke cache, jadi lagu yang sempat gagal akan dicoba lagi saat diputar berikutnya.

| Shortcut | Fungsi |
|---|---|
| `Ctrl+Alt+L` | Nyalakan/matikan kunci & tembus klik |
| `Ctrl+Alt+K` | Tampilkan/sembunyikan jendela |

## Data

Semua tersimpan di `%LOCALAPPDATA%\SpotifyLyricsFloat\`:

- `settings.json`: posisi, ukuran, warna kaca, status kunci, offset per lagu
- `lyrics\`: cache lirik (lagu yang liriknya tidak ditemukan dicoba lagi setelah 1 hari)
- `app.log`: log untuk debugging

## Keterbatasan

- Hanya membaca Spotify **desktop** di PC ini, bukan Spotify yang diputar di HP atau web player.
- Tidak semua lagu ada di LRCLIB. Kalau hanya ada lirik tanpa timestamp, baris disebar rata sepanjang lagu dan header menampilkan "tanpa sinkron".
- Iklan Spotify Free dideteksi dengan heuristik (artis kosong atau "Spotify").
- Jendela overlay tidak bisa tampil di atas game *exclusive fullscreen* (mode borderless/windowed aman).

## Struktur

```
src/LyricsFloat.Core/     logika murni: parser LRC, timeline, clock, client LRCLIB, cache
src/LyricsFloat.App/      aplikasi WPF: jendela, SMTC watcher, tray, hotkey, settings
tests/LyricsFloat.Core.Tests/
tools/SmtcProbe/          alat diagnosa: tampilkan data SMTC Spotify  (dotnet run --project tools\SmtcProbe -- 10)
```
