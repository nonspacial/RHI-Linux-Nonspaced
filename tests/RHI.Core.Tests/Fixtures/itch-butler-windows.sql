-- Butler database of the Windows itch app inside a Wine prefix, for ItchDetectionTests (Refs #90): Windows paths, one
-- library on drive D:. Regenerate: python3 -c 'import sqlite3; c=sqlite3.connect("itch-butler-windows.db"); c.executescript(open("itch-butler-windows.sql").read()); c.commit()'
CREATE TABLE "games" ("id" INTEGER NOT NULL, "url" TEXT, "title" TEXT, "short_text" TEXT, "type" TEXT, "classification" TEXT, PRIMARY KEY ("id"));
CREATE TABLE "install_locations" ("id" TEXT NOT NULL, "path" TEXT, PRIMARY KEY ("id"));
CREATE TABLE "caves" ("id" TEXT NOT NULL, "game_id" INTEGER, "external_game_id" INTEGER, "upload_id" INTEGER, "build_id" INTEGER, "morphing" BOOLEAN, "pinned" BOOLEAN, "installed_at" DATETIME, "last_touched_at" DATETIME, "seconds_run" INTEGER, "local_seconds_run" INTEGER, "local_last_run_at" DATETIME, "snoozed_at" DATETIME, "verdict" TEXT, "settings" TEXT, "installed_size" INTEGER, "install_location_id" TEXT, "install_folder_name" TEXT, "custom_install_folder" TEXT, PRIMARY KEY ("id"));
INSERT INTO games VALUES (3003, 'https://studio.itch.io/fleet', 'Tiny Fleet', 'Ships', 'default', 'game');
INSERT INTO install_locations VALUES ('loc-d', 'D:\itch\library');
INSERT INTO caves VALUES ('cave-fleet', 3003, 0, 1, 1, 0, 0, NULL, NULL, 0, 0, NULL, NULL,
  '{"basePath":"D:\\itch\\library\\tiny-fleet","totalSize":100,"candidates":[{"path":"Launcher\\Game.exe","depth":2,"flavor":"windows","arch":"amd64","size":100}]}',
  '{}', 100, 'loc-d', 'tiny-fleet', '');
