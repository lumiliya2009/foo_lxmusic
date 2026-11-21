# Changelog

## 0.1.12

- Ask for confirmation before replacing a source during an update check, and show the result in a dialog.
- Download component updates from jsDelivr mirrors first and GitHub as the final fallback.

## 0.1.11

- Edit imported source scripts directly, including configured cookies.
- Remove the confirmation dialog shown before importing a source.
- Show source version, author, description, supported platforms and qualities.
- Check paid-source updates through LX `updateAlert`, or open the provided update page.
- Copy the address of a source imported from a URL.
- Use status messages for update checks and direct-link copies.
- Add open-folder and close buttons to the download completion dialog.

## 0.1.10

- Download all search pages with configurable parallel tasks, failure skipping and post-download duplicate cleanup.
- Add a preferences page and optional compact download dialog.
- Check for component updates automatically with fallback feeds and connection routes.
- Copy a selected track's playback URL from its context menu.

## 0.1.9

- Choose or customize download filenames with a preview and saved preferences.

## 0.1.8

- Add search pagination and page jumps in result lists.
- Save search history and keep multiple result lists.
- Download updates from GitHub and install them on restart, with package verification and rollback.

## 0.1.7

- Save multiple sources, switch them in search, and list supported searchable platforms.
- Add source management, Kuwo/Migu track search and a GitHub update check.
- Improve LX script compatibility and fix playback fallback with decoders built without RTTI.

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
