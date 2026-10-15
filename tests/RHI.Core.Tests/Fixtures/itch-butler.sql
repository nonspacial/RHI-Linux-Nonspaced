-- Butler (itch app) database fixture for ItchLibraryTests (Refs #90). Schema as hades creates it (table-level
-- PRIMARY KEY, so games.id is a rowid alias). Two install locations: the native app's default apps folder is not in
-- the table (butler adds it as a location too, here a second library under "/fixture/library").
-- Regenerate: rm itch-butler.db && sqlite3 itch-butler.db < itch-butler.sql   (or python3 -c 'import sqlite3,sys; c=sqlite3.connect("itch-butler.db"); c.executescript(open("itch-butler.sql").read()); c.commit()')
CREATE TABLE "games" ("id" INTEGER NOT NULL, "url" TEXT, "title" TEXT, "short_text" TEXT, "type" TEXT, "classification" TEXT, PRIMARY KEY ("id"));
CREATE TABLE "install_locations" ("id" TEXT NOT NULL, "path" TEXT, PRIMARY KEY ("id"));
CREATE TABLE "caves" ("id" TEXT NOT NULL, "game_id" INTEGER, "external_game_id" INTEGER, "upload_id" INTEGER, "build_id" INTEGER, "morphing" BOOLEAN, "pinned" BOOLEAN, "installed_at" DATETIME, "last_touched_at" DATETIME, "seconds_run" INTEGER, "local_seconds_run" INTEGER, "local_last_run_at" DATETIME, "snoozed_at" DATETIME, "verdict" TEXT, "settings" TEXT, "installed_size" INTEGER, "install_location_id" TEXT, "install_folder_name" TEXT, "custom_install_folder" TEXT, PRIMARY KEY ("id"));
INSERT INTO games VALUES (1001, 'https://studio.itch.io/lantern', 'Lantern Keeper', 'A lighthouse', 'default', 'game');
INSERT INTO games VALUES (1002, 'https://studio.itch.io/moss', 'Moss Garden', 'Grow', 'default', 'game');
INSERT INTO games VALUES (1003, 'https://studio.itch.io/web', 'Web Toy', 'Browser', 'default', 'game');
INSERT INTO install_locations VALUES ('loc-library', '/fixture/library');
INSERT INTO caves VALUES ('cave-lantern', 1001, 0, 1, 1, 0, 0, NULL, NULL, 0, 0, NULL, NULL,
  '{"basePath":"/fixture/library/lantern-keeper","totalSize":100,"candidates":[{"path":"bin/Lantern.exe","depth":2,"flavor":"windows","arch":"amd64","size":100}]}',
  '{}', 100, 'loc-library', 'lantern-keeper', '');
INSERT INTO caves VALUES ('cave-moss', 1002, 0, 2, 2, 0, 0, NULL, NULL, 0, 0, NULL, NULL,
  '{"basePath":"/fixture/custom/Moss","totalSize":100,"candidates":[{"path":"moss.x86_64","depth":1,"flavor":"linux","arch":"amd64","size":100}]}',
  '{}', 100, '', '', '/fixture/custom/Moss');
INSERT INTO caves VALUES ('cave-web', 1003, 0, 3, 3, 0, 0, NULL, NULL, 0, 0, NULL, NULL,
  '{"basePath":"/fixture/library/web-toy","totalSize":10,"candidates":[{"path":"index.html","depth":1,"flavor":"html","size":10}]}',
  '{}', 10, 'loc-library', 'web-toy', '');
INSERT INTO caves VALUES ('cave-escape', 1001, 0, 4, 4, 0, 0, NULL, NULL, 0, 0, NULL, NULL, NULL, '{}', 1, 'loc-library', '../outside', '');
