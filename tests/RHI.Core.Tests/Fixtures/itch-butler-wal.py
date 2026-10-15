#!/usr/bin/env python3
"""Generates itch-butler-wal.db + itch-butler-wal.db-wal (Refs #90, w4 review minor 3): butler's database as it sits on
disk while the itch app runs. The main file holds one checkpointed game (Lantern Keeper); the -wal file holds two more
committed transactions: Moss Garden is installed, then Lantern Keeper is renamed. Run in this folder."""
import os, shutil, sqlite3, tempfile

work = tempfile.mkdtemp()
db = os.path.join(work, "butler.db")
c = sqlite3.connect(db, isolation_level=None)
c.execute("PRAGMA journal_mode=WAL")
c.execute("PRAGMA wal_autocheckpoint=0")
c.executescript("""
CREATE TABLE "games" ("id" INTEGER NOT NULL, "url" TEXT, "title" TEXT, "short_text" TEXT, "type" TEXT, "classification" TEXT, PRIMARY KEY ("id"));
CREATE TABLE "install_locations" ("id" TEXT NOT NULL, "path" TEXT, PRIMARY KEY ("id"));
CREATE TABLE "caves" ("id" TEXT NOT NULL, "game_id" INTEGER, "verdict" TEXT, "install_location_id" TEXT, "install_folder_name" TEXT, "custom_install_folder" TEXT, PRIMARY KEY ("id"));
INSERT INTO games VALUES (1001, 'https://studio.itch.io/lantern', 'Lantern Keeper', '', 'default', 'game');
INSERT INTO install_locations VALUES ('loc-library', '/fixture/library');
INSERT INTO caves VALUES ('cave-lantern', 1001, NULL, 'loc-library', 'lantern-keeper', '');
""")
c.execute("PRAGMA wal_checkpoint(TRUNCATE)")
c.execute("BEGIN")
c.execute("INSERT INTO games VALUES (1002, 'https://studio.itch.io/moss', 'Moss Garden', '', 'default', 'game')")
c.execute("INSERT INTO caves VALUES ('cave-moss', 1002, NULL, 'loc-library', 'moss', '')")
c.execute("COMMIT")
c.execute("UPDATE games SET title = 'Lantern Keeper Deluxe' WHERE id = 1001")
# Copy while the connection is open: closing it would checkpoint and delete the -wal file.
shutil.copyfile(db, "itch-butler-wal.db")
shutil.copyfile(db + "-wal", "itch-butler-wal.db-wal")
c.close()
shutil.rmtree(work)
