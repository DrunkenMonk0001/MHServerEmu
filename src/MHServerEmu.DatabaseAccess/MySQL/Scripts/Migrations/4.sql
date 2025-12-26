-- Add guild data and LastLogoutTime (schema version 5)

ALTER TABLE Player ADD COLUMN LastLogoutTime BIGINT;
UPDATE Player SET LastLogoutTime=0;

CREATE TABLE IF NOT EXISTS Guild (
    Id BIGINT PRIMARY KEY,
    Name VARCHAR(255) NOT NULL UNIQUE,
    Motd VARCHAR(255) NOT NULL,
    CreatorDbGuid BIGINT,
    CreationTime BIGINT
);

CREATE TABLE IF NOT EXISTS GuildMember (
    PlayerDbGuid BIGINT PRIMARY KEY,
    GuildId BIGINT NOT NULL,
    Membership BIGINT NOT NULL,
    FOREIGN KEY (PlayerDbGuid) REFERENCES Account(Id) ON DELETE CASCADE,
    FOREIGN KEY (GuildId) REFERENCES Guild(Id) ON DELETE CASCADE
);

-- Update schema version to 5
UPDATE SchemaVersion SET schema_version = 5;