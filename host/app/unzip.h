// The game's one-file install: Documents/port17-game.zip (the game folder packed on the PC)
// unpacks itself into Documents/game on first launch — a single drag-and-drop instead of a
// thousand small files through the Files app.
//
// The unpacker is a small reader of the zip's central directory (stored and deflate entries,
// which is everything PC zips contain; zip64 is not needed under 4 GB). Deflate decompression
// is the system libz's raw inflate. Paths are made safe (no absolute, no ".."): a zip is data
// from outside. The install is atomic enough for its purpose: a staging directory is renamed
// into place only after the launcher's dll is seen in it.
#ifndef PORT17_UNZIP_H
#define PORT17_UNZIP_H

// Unpacks zipPath into destDir (made if missing). Returns 0, or an errno-style negative.
int port17_unzip(const char *zipPath, const char *destDir);

// The whole install step of the engine mode: a present Documents/port17-game.zip becomes
// Documents/game (the old game folder, if any, is kept as game-previous; the zip is renamed
// to .installed so one copy installs once). On the main thread the unpacking runs on a thread
// of its own while the main run loop keeps pumping (iOS's launch watchdog); elsewhere it is
// done inline. Returns 0 when Documents/game is ready after the call (also when it already
// was), negative when something is missing or failed (the log says what).
int port17_install_game_zip(const char *home);

#endif
