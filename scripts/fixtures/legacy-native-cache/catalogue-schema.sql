-- Empty schema from the original C# writer at 9c104b59. Generated fixture only; no source rows.
CREATE TABLE Blocks(
    Key TEXT NOT NULL REFERENCES Entries(Key) ON DELETE CASCADE,
    Offset INTEGER NOT NULL, Count INTEGER NOT NULL, Hash BLOB NOT NULL,
    PRIMARY KEY(Key, Offset));
CREATE TABLE Entries(
    Key TEXT PRIMARY KEY, Folder TEXT NOT NULL, Length INTEGER NOT NULL,
    Bytes INTEGER NOT NULL DEFAULT 0, Access INTEGER NOT NULL,
    Pinned INTEGER NOT NULL DEFAULT 0, Dirty INTEGER NOT NULL DEFAULT 0,
    ItemId TEXT NOT NULL DEFAULT '',VerifiedBytes INTEGER NOT NULL DEFAULT 0, PendingBytes INTEGER NOT NULL DEFAULT 0, ChunkSize INTEGER NOT NULL DEFAULT 0, Generation TEXT, DisplayName TEXT NOT NULL DEFAULT '', AccessCount INTEGER);
CREATE TABLE FolderIdentity(Folder TEXT PRIMARY KEY, Volume TEXT NOT NULL, RootIdentity TEXT);
CREATE TABLE FolderTotals(Folder TEXT PRIMARY KEY, Bytes INTEGER NOT NULL DEFAULT 0);
CREATE TABLE ModificationTimes(ItemId TEXT PRIMARY KEY, Generation TEXT NOT NULL, Seconds INTEGER NOT NULL);
CREATE TABLE RetiredEntries(Key TEXT NOT NULL, Folder TEXT NOT NULL, Bytes INTEGER NOT NULL, ItemId TEXT NOT NULL DEFAULT '', DisplayName TEXT NOT NULL DEFAULT '', Length INTEGER NOT NULL DEFAULT 0, VerifiedBytes INTEGER NOT NULL DEFAULT 0, AccessCount INTEGER, LastAccessUnix INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(Key,Folder));
CREATE TRIGGER BlockAdded AFTER INSERT ON Blocks BEGIN
    UPDATE Entries SET VerifiedBytes=VerifiedBytes+NEW.Count WHERE Key=NEW.Key;
END;
CREATE TRIGGER BlockRemoved AFTER DELETE ON Blocks BEGIN
    UPDATE Entries SET VerifiedBytes=VerifiedBytes-OLD.Count WHERE Key=OLD.Key;
END;
CREATE TRIGGER EntryAdded AFTER INSERT ON Entries BEGIN
    INSERT INTO FolderTotals(Folder,Bytes) VALUES(NEW.Folder,NEW.Bytes)
        ON CONFLICT(Folder) DO UPDATE SET Bytes=Bytes+NEW.Bytes;
END;
CREATE TRIGGER EntryChanged AFTER UPDATE OF Bytes ON Entries BEGIN
    UPDATE FolderTotals SET Bytes=Bytes+NEW.Bytes-OLD.Bytes WHERE Folder=NEW.Folder;
END;
CREATE TRIGGER EntryRemoved AFTER DELETE ON Entries BEGIN
    UPDATE FolderTotals SET Bytes=Bytes-OLD.Bytes WHERE Folder=OLD.Folder;
END;
CREATE TRIGGER RetiredAdded AFTER INSERT ON RetiredEntries BEGIN
    INSERT INTO FolderTotals(Folder,Bytes) VALUES(NEW.Folder,NEW.Bytes)
        ON CONFLICT(Folder) DO UPDATE SET Bytes=Bytes+NEW.Bytes;
END;
CREATE TRIGGER RetiredRemoved AFTER DELETE ON RetiredEntries BEGIN
    UPDATE FolderTotals SET Bytes=Bytes-OLD.Bytes WHERE Folder=OLD.Folder;
END;
