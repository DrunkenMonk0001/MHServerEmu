-- Initialize a new database file using the current schema version

CREATE TABLE IF NOT EXISTS Leaderboards (
    LeaderboardId BIGINT PRIMARY KEY,
    PrototypeName VARCHAR(255),
    ActiveInstanceId BIGINT,
    IsEnabled TINYINT,
    StartTime BIGINT,
    MaxResetCount BIGINT
);

CREATE TABLE IF NOT EXISTS Instances (
    InstanceId BIGINT PRIMARY KEY,
    LeaderboardId BIGINT,
    State INT,
    ActivationDate BIGINT,
    Visible TINYINT,
    FOREIGN KEY (LeaderboardId) REFERENCES Leaderboards(LeaderboardId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Entries (
    InstanceId BIGINT,
    ParticipantId BIGINT,
    Score INT,
    HighScore INT,
    RuleStates LONGBLOB,
    PRIMARY KEY (InstanceId, ParticipantId),
    FOREIGN KEY (InstanceId) REFERENCES Instances(InstanceId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS MetaEntries (
    LeaderboardId BIGINT,
    InstanceId BIGINT,
    SubLeaderboardId BIGINT,
    SubInstanceId BIGINT,
    PRIMARY KEY (LeaderboardId, InstanceId, SubLeaderboardId),
    FOREIGN KEY (LeaderboardId) REFERENCES Leaderboards(LeaderboardId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Rewards (
    LeaderboardId BIGINT,
    InstanceId BIGINT,
    ParticipantId BIGINT,
    `Rank` INT,
    RewardId BIGINT,
    CreationDate BIGINT,
    RewardedDate BIGINT,
    PRIMARY KEY (LeaderboardId, InstanceId, ParticipantId),
    FOREIGN KEY (InstanceId) REFERENCES Instances(InstanceId) ON DELETE CASCADE
);

CREATE INDEX idx_instances_leaderboardid ON Instances (LeaderboardId);
CREATE INDEX idx_entries_instanceid ON Entries (InstanceId);
CREATE INDEX idx_rewards_participantid ON Rewards (ParticipantId);
