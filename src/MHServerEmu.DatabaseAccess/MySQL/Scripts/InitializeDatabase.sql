CREATE TABLE IF NOT EXISTS SchemaVersion (
    schema_version INT NOT NULL
);

INSERT INTO SchemaVersion (schema_version) VALUES (2);

CREATE TABLE IF NOT EXISTS Account (
    Id BIGINT PRIMARY KEY,
    Email VARCHAR(255) NOT NULL UNIQUE,
    PlayerName VARCHAR(255) NOT NULL UNIQUE,
    PasswordHash BLOB NOT NULL,
    Salt BLOB NOT NULL,
    UserLevel TINYINT NOT NULL,
    Flags INT NOT NULL
);

CREATE TABLE IF NOT EXISTS Player (
    DbGuid BIGINT PRIMARY KEY,
    ContainerId BIGINT,
    ArchiveData LONGBLOB,
    StartTarget BIGINT,
    StartTargetRegionOverride BIGINT,
    AOIVolume INT,
    FOREIGN KEY (DbGuid) REFERENCES Account(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Avatar (
    DbGuid BIGINT PRIMARY KEY,
    ContainerDbGuid BIGINT,
    InventoryProtoGuid BIGINT,
    Slot INT UNSIGNED,
    EntityProtoGuid BIGINT,
    ArchiveData LONGBLOB,
    FOREIGN KEY (ContainerDbGuid) REFERENCES Player(DbGuid) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS TeamUp (
    DbGuid BIGINT PRIMARY KEY,
    ContainerDbGuid BIGINT,
    InventoryProtoGuid BIGINT,
    Slot INT UNSIGNED,
    EntityProtoGuid BIGINT,
    ArchiveData LONGBLOB,
    FOREIGN KEY (ContainerDbGuid) REFERENCES Player(DbGuid) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Item (
    DbGuid BIGINT PRIMARY KEY,
    ContainerDbGuid BIGINT,
    InventoryProtoGuid BIGINT,
    Slot INT UNSIGNED,
    EntityProtoGuid BIGINT,
    ArchiveData LONGBLOB,
    FOREIGN KEY (ContainerDbGuid) REFERENCES Player(DbGuid) ON DELETE CASCADE,
    FOREIGN KEY (ContainerDbGuid) REFERENCES Avatar(DbGuid) ON DELETE CASCADE,
    FOREIGN KEY (ContainerDbGuid) REFERENCES TeamUp(DbGuid) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ControlledEntity (
    DbGuid BIGINT PRIMARY KEY,
    ContainerDbGuid BIGINT,
    InventoryProtoGuid BIGINT,
    Slot INT UNSIGNED,
    EntityProtoGuid BIGINT,
    ArchiveData LONGBLOB,
    FOREIGN KEY (ContainerDbGuid) REFERENCES Avatar(DbGuid) ON DELETE CASCADE
);

CREATE INDEX IX_Avatar_ContainerDbGuid ON Avatar (ContainerDbGuid);
CREATE INDEX IX_TeamUp_ContainerDbGuid ON TeamUp (ContainerDbGuid);
CREATE INDEX IX_Item_ContainerDbGuid ON Item (ContainerDbGuid);
CREATE INDEX IX_ControlledEntity_ContainerDbGuid ON ControlledEntity (ContainerDbGuid);
