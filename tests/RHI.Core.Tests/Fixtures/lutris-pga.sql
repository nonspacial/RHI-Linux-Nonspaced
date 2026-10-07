-- Source of lutris-pga.db (regenerate: rm lutris-pga.db && sqlite3 lutris-pga.db < lutris-pga.sql).
-- Lutris' games table as of 0.5.x, minus discord_id, which is added later as Lutris migrations do,
-- so old rows are shorter than the schema. 300 filler rows force interior b-tree pages and one
-- 5000-character name forces an overflow page.
PRAGMA page_size = 4096;
CREATE TABLE games (id INTEGER PRIMARY KEY, name TEXT, sortname TEXT, slug TEXT, installer_slug TEXT, parent_slug TEXT, platform TEXT, runner TEXT, executable TEXT, directory TEXT, updated DATETIME, lastplayed INTEGER, installed INTEGER, installed_at INTEGER, year INTEGER, configpath TEXT, has_custom_banner INTEGER, has_custom_icon INTEGER, has_custom_coverart_big INTEGER, playtime REAL, service TEXT, service_id TEXT);
INSERT INTO games (name, slug, runner, directory, installed, configpath, playtime) VALUES ('Battle.net', 'battlenet', 'wine', '/games/battlenet', 1, 'battlenet-1774722738', 1.5);
INSERT INTO games (name, slug, runner, directory, installed, configpath) VALUES ('Ablechum12', 'ablechum12', 'wine', '', 1, 'ablechum12-1775064156');
INSERT INTO games (name, slug, runner, directory, installed, configpath) VALUES ('Gone Game', 'gone-game', 'wine', '/games/gone', 0, 'gone-game-1700000000');
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300)
INSERT INTO games (name, slug, runner, directory, installed, configpath) SELECT 'Filler ' || i, 'filler-' || i, 'linux', '/games/filler-' || i, 1, 'filler-' || i || '-1700000100' FROM n;
ALTER TABLE games ADD COLUMN discord_id TEXT;
INSERT INTO games (name, slug, runner, directory, installed, configpath, discord_id) VALUES (printf('%.5000c', 'L'), 'long-name', 'linux', '/games/long', 1, 'long-name-1700000001', '42');
