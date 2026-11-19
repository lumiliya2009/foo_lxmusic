# Changelog

## 0.1.6

- Show the decoder's actual sample rate, bitrate, channels and codec during playback.
- Try other available qualities of the same song when its media URL is rejected or invalid, resuming at the current position when possible.
- Stop with one error after available qualities fail; avoid repeating failed qualities and URLs.

## 0.1.5

- Download an entire album directly from its context menu, including all catalog pages.
- Store albums in separate directories with ordered track filenames and a cover image.
- Remember download directory and quality; preserve existing files and directories.

## 0.1.4

- Detect FLAC, Ogg, WAV and MP4 from file bytes before selecting a decoder, including FLAC streams incorrectly labeled as Ogg by the server.

## 0.1.3

- Open album, artist and playlist results with Enter or double-click.
- Stop and show an error when online playback fails, without automatically skipping failed songs.
- Remember search options and focus the first result.

## 0.1.2

- Search online tracks and collections, import LX sources, switch playback quality, retrieve lyrics and download selected tracks.

## Source publication preparation

- Include build instructions, dependency locks and component-specific license notices.
- Exclude local profiles, downloaded media, imported source configuration and build artifacts from the source repository.
