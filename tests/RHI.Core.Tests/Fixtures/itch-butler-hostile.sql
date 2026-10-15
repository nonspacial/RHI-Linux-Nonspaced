-- Hostile butler (itch app) database for ItchDetectionTests (Refs #90, w4 review minor 1): install folders that are the
-- filesystem root, the home folder, home containers and a Wine prefix, and launch candidates that climb out of the game
-- folder (`..` out of the folder or within it, `.`, a symlinked subfolder, `..` after a symlink). Paths under "/fixture/" are mapped onto the test tree.
-- Regenerate: rm itch-butler-hostile.db && python3 -c 'import sqlite3; c=sqlite3.connect("itch-butler-hostile.db"); c.executescript(open("itch-butler-hostile.sql").read()); c.commit()'
CREATE TABLE "games" ("id" INTEGER NOT NULL, "url" TEXT, "title" TEXT, "short_text" TEXT, "type" TEXT, "classification" TEXT, PRIMARY KEY ("id"));
CREATE TABLE "install_locations" ("id" TEXT NOT NULL, "path" TEXT, PRIMARY KEY ("id"));
CREATE TABLE "caves" ("id" TEXT NOT NULL, "game_id" INTEGER, "external_game_id" INTEGER, "upload_id" INTEGER, "build_id" INTEGER, "morphing" BOOLEAN, "pinned" BOOLEAN, "installed_at" DATETIME, "last_touched_at" DATETIME, "seconds_run" INTEGER, "local_seconds_run" INTEGER, "local_last_run_at" DATETIME, "snoozed_at" DATETIME, "verdict" TEXT, "settings" TEXT, "installed_size" INTEGER, "install_location_id" TEXT, "install_folder_name" TEXT, "custom_install_folder" TEXT, PRIMARY KEY ("id"));
INSERT INTO games VALUES (4001, 'https://x.itch.io/root', 'Whole Disk', '', 'default', 'game');
INSERT INTO games VALUES (4002, 'https://x.itch.io/home', 'Whole Home', '', 'default', 'game');
INSERT INTO games VALUES (4003, 'https://x.itch.io/wine', 'Wine Folder', '', 'default', 'game');
INSERT INTO games VALUES (4004, 'https://x.itch.io/games', 'Games Folder', '', 'default', 'game');
INSERT INTO games VALUES (4005, 'https://x.itch.io/prefix', 'Prefix Root', '', 'default', 'game');
INSERT INTO games VALUES (4006, 'https://x.itch.io/escape', 'Escape Artist', '', 'default', 'game');
INSERT INTO install_locations VALUES ('loc-home', '/fixture/home');
INSERT INTO install_locations VALUES ('loc-library', '/fixture/library');
INSERT INTO caves VALUES ('cave-root', 4001, 0, 1, 1, 0, 0, NULL, NULL, 0, 0, NULL, NULL, NULL, '{}', 1, '', '', '/');
INSERT INTO caves VALUES ('cave-home', 4002, 0, 2, 2, 0, 0, NULL, NULL, 0, 0, NULL, NULL, NULL, '{}', 1, '', '', '/fixture/home');
INSERT INTO caves VALUES ('cave-wine', 4003, 0, 3, 3, 0, 0, NULL, NULL, 0, 0, NULL, NULL, NULL, '{}', 1, 'loc-home', '.wine', '');
INSERT INTO caves VALUES ('cave-games', 4004, 0, 4, 4, 0, 0, NULL, NULL, 0, 0, NULL, NULL, NULL, '{}', 1, '', '', '/fixture/home/Games/');
INSERT INTO caves VALUES ('cave-prefix', 4005, 0, 5, 5, 0, 0, NULL, NULL, 0, 0, NULL, NULL, NULL, '{}', 1, 'loc-library', 'pfx', '');
INSERT INTO caves VALUES ('cave-escape', 4006, 0, 6, 6, 0, 0, NULL, NULL, 0, 0, NULL, NULL,
  '{"basePath":"/fixture/library/escape","totalSize":100,"candidates":[{"path":"../outside/Evil.exe","flavor":"windows"},{"path":"linked/../Evil.exe","flavor":"windows"},{"path":"bin/../Dot.exe","flavor":"windows"},{"path":"./Dot.exe","flavor":"windows"},{"path":"linked/Evil.exe","flavor":"windows"},{"path":"bin\\Good.exe","flavor":"windows"}]}',
  '{}', 100, 'loc-library', 'escape', '');
