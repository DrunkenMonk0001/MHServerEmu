using Dapper;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.MySqlDB;
using MySqlConnector;
using MHServerEmu.Core.Helpers;
using MHServerEmu.DatabaseAccess.SQLite;
using System.Data.SQLite;
using System.Text;
using System.Diagnostics;
using MHServerEmu.Core.System.Time;


namespace MHServerEmu.DatabaseAccess.MySQL
{
    /// <summary>
    /// Provides functionality for storing <see cref="DBAccount"/> instances in a MySql database using the <see cref="IDBManager"/> interface.
    /// </summary>
    public class MySQLDBManager : IDBManager
    {
        private const int CurrentSchemaVersion = 5;         // Increment this when making changes to the database schema (updated to match SQLite)
        private const int NumTestAccounts = 5;              // Number of test accounts to create for new databases
        private const int NumPlayerDataWriteAttempts = 3;   // Number of write attempts to do when saving player data
        public bool isSchemaExist;

        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly object _writeLock = new();

        private string _dbFilePath;
        private string _connectionString;

        // Backup management added to match SQLiteDBManager feature set.
        private int _maxBackupNumber;
        private CooldownTimer _backupTimer;
        private volatile bool _backupInProgress;

        public static MySQLDBManager Instance { get; } = new();
        private MySQLDBManager() { }

        public bool Initialize()
        {
            var config = ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>();

            // Ensure file path is assigned before building any connection string that may use it
            _dbFilePath = Path.Combine(FileHelper.DataDirectory, ConfigManager.Instance.GetConfig<SQLiteDBManagerConfig>().FileName);
            _connectionString = $"Data Source={_dbFilePath}";

            // Initialize backup settings (use same config keys as SQLite for now to retain parity)
            var sqliteConfig = ConfigManager.Instance.GetConfig<SQLiteDBManagerConfig>();
            _maxBackupNumber = sqliteConfig.MaxBackupNumber;
            _backupTimer = new(TimeSpan.FromMinutes(sqliteConfig.BackupIntervalMinutes));
            _backupInProgress = false;

            using MySqlConnection connection = GetConnection();
            int schemaVersion = GetSchemaVersion(connection);
            var findSchema = connection.Query("SHOW DATABASES LIKE '" + config.MySqlDBName + "';");
            connection.Close();
            isSchemaExist = findSchema.Any();
            if (schemaVersion < CurrentSchemaVersion) MigrateDatabaseFileToCurrentSchema();
            try
            {
                if (!isSchemaExist) InitializeDatabaseFile();
                if (File.Exists(_dbFilePath) == true & !isSchemaExist)
                {
                    if (MigrateDatabaseFileToCurrentSchema() == false)
                        return false;
                    return true;
                }
                    //_lastBackupTime = Clock.GameTime;
                return true;
            }
            catch
            {
                if (File.Exists(_dbFilePath) == true & !isSchemaExist)
                {
                    // Migrate existing database if needed
                    if (MigrateDatabaseFileToCurrentSchema() == false)
                        return false;
                }
                else
                {
                    if (InitializeDatabaseFile() == false)
                        return false;
                }
                Logger.Info($"Connected to Database");
                return true;
            }
        }
        public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
        {
            using MySqlConnection connection = GetConnection();

            // Use an utf8mb4 collation to match servers/tables using utf8mb4.
            var account = connection.QueryFirstOrDefault<DBAccount>(
                "SELECT Id, PlayerName FROM Account WHERE PlayerName = @PlayerName COLLATE utf8mb4_general_ci",
                new { PlayerName = playerName });

            if (account == null)
            {
                playerDbId = 0;
                playerNameOut = null;
                return false;
            }

            playerDbId = (ulong)account.Id;
            playerNameOut = account.PlayerName;
            return true;
        }


        public bool TryQueryAccountByEmail(string email, out DBAccount account)
        {
            using MySqlConnection connection = GetConnection();
            // Use utf8mb4 collation for case-insensitive comparisons to avoid charset/collation mismatch errors.
            var accounts = connection.Query<DBAccount>("SELECT * FROM Account WHERE Email = @Email COLLATE utf8mb4_general_ci", new { Email = email });

            account = accounts.FirstOrDefault();
            return account != null;
        }

        public bool TryGetPlayerName(ulong id, out string playerName)
        {
            using MySqlConnection connection = GetConnection();

            playerName = connection.QueryFirstOrDefault<string>("SELECT PlayerName FROM Account WHERE Id = @Id", new { Id = (long)id });

            return string.IsNullOrWhiteSpace(playerName) == false;
        }

        public bool GetPlayerNames(Dictionary<ulong, string> playerNames)
        {
            using MySqlConnection connection = GetConnection();

            var accounts = connection.Query<DBAccount>("SELECT Id, PlayerName FROM Account");

            foreach (DBAccount account in accounts)
                playerNames[(ulong)account.Id] = account.PlayerName;

            return playerNames.Count > 0;
        }

        public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime)
        {
            using MySqlConnection connection = GetConnection();

            // Ensure Player table includes LastLogoutTime column in your MySQL schema / migrations.
            lastLogoutTime = connection.QueryFirstOrDefault<long>("SELECT LastLogoutTime FROM Player WHERE DbGuid = @DbGuid", new { DbGuid = (long)playerDbId });

            return lastLogoutTime > 0;
        }

        public bool InsertAccount(DBAccount account)
        {
            lock (_writeLock)
            {
                using MySqlConnection connection = GetConnection();
                try
                {
                    connection.Execute(@"INSERT INTO Account (Id, Email, PlayerName, PasswordHash, Salt, UserLevel, Flags)
                        VALUES (@Id, @Email, @PlayerName, @PasswordHash, @Salt, @UserLevel, @Flags)", account);
                    return true;
                }
                catch (Exception e)
                {
                    Logger.ErrorException(e, nameof(InsertAccount));
                    return false;
                }
            }
        }

        public bool UpdateAccount(DBAccount account)
        {
            lock (_writeLock)
            {
                using MySqlConnection connection = GetConnection();
                try
                {
                    connection.Execute(@"UPDATE Account SET Email=@Email, PlayerName=@PlayerName, PasswordHash=@PasswordHash, Salt=@Salt,
                        UserLevel=@UserLevel, Flags=@Flags WHERE Id=@Id", account);
                    return true;
                }
                catch (Exception e)
                {
                    Logger.ErrorException(e, nameof(UpdateAccount));
                    return false;
                }
            }
        }

        public bool LoadPlayerData(DBAccount account)
        {
            account.Player = null;
            account.ClearEntities();

            using MySqlConnection connection = GetConnection();

            account.Player = connection.QueryFirstOrDefault<DBPlayer>("SELECT * FROM Player WHERE DbGuid = @DbGuid", new { DbGuid = account.Id });
            if (account.Player == null)
            {
                account.Player = new(account.Id);
                Logger.Info($"Initialized player data for account 0x{account.Id:X}");
            }

            // Use MySQLEntityTable helpers for loading ONLY (mimic SQLiteDBManager)
            var avatarTable = MySQLEntityTable.GetTable(DBEntityCategory.Avatar);
            var teamUpTable = MySQLEntityTable.GetTable(DBEntityCategory.TeamUp);
            var itemTable = MySQLEntityTable.GetTable(DBEntityCategory.Item);
            var controlledEntityTable = MySQLEntityTable.GetTable(DBEntityCategory.ControlledEntity);

            avatarTable.LoadEntities(connection, account.Id, account.Avatars);
            teamUpTable.LoadEntities(connection, account.Id, account.TeamUps);
            itemTable.LoadEntities(connection, account.Id, account.Items);

            foreach (DBEntity avatar in account.Avatars)
            {
                itemTable.LoadEntities(connection, avatar.DbGuid, account.Items);
                controlledEntityTable.LoadEntities(connection, avatar.DbGuid, account.ControlledEntities);
            }

            foreach (DBEntity teamUp in account.TeamUps)
            {
                itemTable.LoadEntities(connection, teamUp.DbGuid, account.Items);
            }

            return true;
        }

        private bool DoSavePlayerData(DBAccount account)
        {
            lock (_writeLock)
            {
                using MySqlConnection connection = GetConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                try
                {
                    connection.Execute("SET FOREIGN_KEY_CHECKS=0;", transaction: transaction);
                    if (account.Player != null)
                    {
                        connection.Execute(@$"INSERT IGNORE INTO Player (DbGuid) VALUES (@DbGuid)", account.Player, transaction);
                        connection.Execute(@$"UPDATE Player SET ArchiveData=@ArchiveData, StartTarget=@StartTarget,
                                            AOIVolume=@AOIVolume, GazillioniteBalance=@GazillioniteBalance, LastLogoutTime=@LastLogoutTime WHERE DbGuid = @DbGuid",
                                            account.Player, transaction);
                    }
                    else
                    {
                        Logger.Warn($"DoSavePlayerData(): Attempted to save null player entity data for account {account}");
                    }

                    // Use only MySQLEntityTable helpers for saving, no legacy helpers
                    var avatarTable = MySQLEntityTable.GetTable(DBEntityCategory.Avatar);
                    var teamUpTable = MySQLEntityTable.GetTable(DBEntityCategory.TeamUp);
                    var itemTable = MySQLEntityTable.GetTable(DBEntityCategory.Item);
                    var controlledEntityTable = MySQLEntityTable.GetTable(DBEntityCategory.ControlledEntity);

                    avatarTable.UpdateEntities(connection, transaction, account.Id, account.Avatars);
                    teamUpTable.UpdateEntities(connection, transaction, account.Id, account.TeamUps);
                    itemTable.UpdateEntities(connection, transaction, account.Id, account.Items);

                    foreach (DBEntity avatar in account.Avatars)
                    {
                        itemTable.UpdateEntities(connection, transaction, avatar.DbGuid, account.Items);
                        controlledEntityTable.UpdateEntities(connection, transaction, avatar.DbGuid, account.ControlledEntities);
                    }

                    foreach (DBEntity teamUp in account.TeamUps)
                    {
                        itemTable.UpdateEntities(connection, transaction, teamUp.DbGuid, account.Items);
                    }

                    connection.Execute("SET FOREIGN_KEY_CHECKS=1;", transaction: transaction);
                    transaction.Commit();
                    Logger.Info($"Successfully written player data for account [{account}]");
                    return true;
                }
                catch (Exception e)
                {
                    try { connection.Execute("SET FOREIGN_KEY_CHECKS=1;", transaction: transaction); } catch { }
                    Logger.Warn($"DoSavePlayerData(): MySQL error for account [{account}]: {e.Message}");
                    transaction.Rollback();
                    return false;
                }
            }
        }

        public bool SavePlayerData(DBAccount account)
        {
            for (int i = 0; i < NumPlayerDataWriteAttempts; i++)
            {
                if (DoSavePlayerData(account))
                    return Logger.InfoReturn(true, $"updated player data for account [{account}]");
            }

            return Logger.WarnReturn(false, $"SavePlayerData(): Failed to write player data for account [{account}]");
        }

        /// Creates and opens a new <see cref="MySQLConnection"/>.

        private static MySqlConnection GetConnection()
        {
            var config = ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>();
            var connectionStringVars = string.Join(";", "server="+config.MySqlIP, "port="+config.MySqlPort, "Database="+config.MySqlDBName, "Uid="+config.MySqlUsername, "Pwd="+config.MySqlPw, "SslMode=Required;AllowPublicKeyRetrieval=True;");
            string connectionString = new MySqlConnectionStringBuilder(connectionStringVars).ToString();
            MySqlConnection connection = new(connectionString);
            connection.Open();
            return connection;
        }


        /// Initializes a new empty database file using the current schema.

        private bool InitializeDatabaseFile()
        {
            string MySqlInitializationScript = MySqlScripts.GetInitializationScript();
            var config = ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>();
            if (MySqlInitializationScript == string.Empty)
                return Logger.ErrorReturn(false, "InitializeDatabaseFile(): Failed to get database initialization script");

            var connectionStringVars = string.Join(";", "server=" + config.MySqlIP, "port=" + config.MySqlPort, "Uid=" + config.MySqlUsername, "Pwd=" + config.MySqlPw, "SslMode=Required;AllowPublicKeyRetrieval=True;");
            string connectionString = new MySqlConnectionStringBuilder(connectionStringVars).ToString();
            MySqlConnection connectionInit = new(connectionString);
            connectionInit.Open();
            connectionInit.Execute("CREATE SCHEMA IF NOT EXISTS " + config.MySqlDBName);
            connectionInit.Execute("USE " + config.MySqlDBName);
            connectionInit.Execute(MySqlInitializationScript);

            Logger.Info($"Initialized a new MySql database using schema version {CurrentSchemaVersion}");
            connectionInit.Close();
            CreateTestAccounts(NumTestAccounts);

            return true;
        }

        /// Creates the specified number of test accounts.

        private void CreateTestAccounts(int numAccounts)
        {
            for (int i = 0; i < numAccounts; i++)
            {
                string email = $"test{i + 1}@test.com";
                string playerName = $"Player{i + 1}";
                string password = "123";

                DBAccount account = new(email, playerName, password);
                InsertAccount(account);
                Logger.Info($"Created test account {account}");
            }
        }

        /// Migrates an existing database to the current schema if needed.
        private bool MigrateDatabaseFileToCurrentSchema()
        {
            using MySqlConnection connection = GetConnection();
            using MySqlConnection schemaCheck = GetConnection();
            schemaCheck.Close();
                if (File.Exists(_dbFilePath) == true & !isSchemaExist)
            {
                var config = ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>();
                try
                {
                    File.WriteAllText($"{_dbFilePath}.Migrate.sql", DumpSQLiteDatabase(_dbFilePath, true));
                    string filePath = $"{_dbFilePath}.Migrate.sql";
                    Logger.Info("Importing SQLite Database into MySQL... This may take a while.");
                    string script = File.ReadAllText(filePath);
                    ExecuteSqlScript(connection, script);
                    connection.Close();
                    File.Move($"{_dbFilePath}", $"{_dbFilePath}.Migrated");
                    Logger.Info($"Old SQLite Database saved to {_dbFilePath}.Migrated");
                    File.Delete($"{_dbFilePath}.Migrate.sql");
                    Logger.Info($"SQL to MySQL Migration success!");
                    return true;
                }
                catch (Exception e)
                {
                    if (e.Message.Any())
                    {
                        //Logger.Error(e.Message);
                    }
                    if (!InitializeDatabaseFile()) return false;
                    File.WriteAllText($"{_dbFilePath}.Migrate.sql", DumpSQLiteDatabase(_dbFilePath, true));
                    string filePath = $"{_dbFilePath}.Migrate.sql";
                    Logger.Info("Importing SQLite Database into MySQL... This may take a while.");
                    string script = File.ReadAllText(filePath);
                    ExecuteSqlScript(connection, script);
                    connection.Close();
                    File.Move($"{_dbFilePath}", $"{_dbFilePath}.Migrated");
                    Logger.Info($"Old SQLite Database saved to {_dbFilePath}.Migrated");
                    File.Delete($"{_dbFilePath}.Migrate.sql");
                    Logger.Info($"SQL to MySQL Migration success!");
                    return true;
                }

            }
                
                int schemaVersion = GetSchemaVersion(connection);
                if (schemaVersion > CurrentSchemaVersion)
                    return Logger.ErrorReturn(false, $"Initialize(): Existing database uses unsupported schema version {schemaVersion} (current = {CurrentSchemaVersion})");

                Logger.Info($"Found existing database with schema version {schemaVersion} (current = {CurrentSchemaVersion})");

                if (schemaVersion == CurrentSchemaVersion)
                    return true;

                // Create a backup to fall back to if something goes wrong


                bool success = true;

                while (schemaVersion < CurrentSchemaVersion)
                {
                    Logger.Info($"Migrating version {schemaVersion} => {schemaVersion + 1}...");

                    string migrationScript = MySqlScripts.GetMigrationScript(schemaVersion);
                    if (migrationScript == string.Empty)
                    {
                        Logger.Error($"MigrateDatabaseFileToCurrentSchema(): Failed to get database migration script for version {schemaVersion}");
                        success = false;
                        break;
                    }

                    connection.Execute(migrationScript);
                    SetSchemaVersion(connection, ++schemaVersion);
                }

                success &= GetSchemaVersion(connection) == CurrentSchemaVersion;

                if (success == false)
                {
                    // Restore backup

                    return Logger.ErrorReturn(false, "MigrateDatabaseFileToCurrentSchema(): Migration failed, backup restored");
                }
                else
                {
                    // Clean up backup

                }

                Logger.Info($"Successfully migrated to schema version {CurrentSchemaVersion}");
            
            return true;
        }

        /// Returns the schema_version value of the current database.
        private static int GetSchemaVersion(MySqlConnection connection)
        {
            var dbName = ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>().MySqlDBName;
            var queryResult = connection.Query<int>($"SELECT schema_version FROM {dbName}.SchemaVersion");
            if (queryResult.Any())
                return queryResult.Last();

            return Logger.WarnReturn(-1, "GetSchemaVersion(): Failed to query schema_version from the DB");
        }


        /// Sets the schema_version value of the current database.

        private static void SetSchemaVersion(MySqlConnection connection, int version)
        {
            var dbName = ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>().MySqlDBName;
            connection.Execute($"UPDATE {dbName}.SchemaVersion SET schema_version=@version", new { version });
        }


        /// Query cache for entity operations
        private class EntityQueryCache
        {
            private static readonly Dictionary<DBEntityCategory, EntityQueryCache> QueryDict = new();

            public string SelectAll { get; }
            public string SelectIds { get; }
            public string Delete { get; }
            public string Insert { get; }
            public string Update { get; }

            private EntityQueryCache(DBEntityCategory category)
            {
                SelectAll = @$"SELECT * FROM {category} WHERE ContainerDbGuid = @ContainerDbGuid";
                SelectIds = @$"SELECT DbGuid FROM {category} WHERE ContainerDbGuid = @ContainerDbGuid";
                Delete = @$"DELETE FROM {category} WHERE DbGuid IN @EntitiesToDelete";
                Insert = @$"INSERT IGNORE INTO {category} (DbGuid) VALUES (@DbGuid)";
                Update = @$"UPDATE {category} SET ContainerDbGuid=@ContainerDbGuid, InventoryProtoGuid=@InventoryProtoGuid,
                                   Slot=@Slot, EntityProtoGuid=@EntityProtoGuid, ArchiveData=@ArchiveData WHERE DbGuid=@DbGuid";
            }

            public static EntityQueryCache GetQueries(DBEntityCategory category)
            {
                if (QueryDict.TryGetValue(category, out EntityQueryCache queries) == false)
                {
                    queries = new(category);
                    QueryDict.Add(category, queries);
                }
                return queries;
            }
        }

        public static string DumpSQLiteDatabase(string dbFilePath, bool isExist)
        {
                StringBuilder tableCreation = new();
                StringBuilder foreignKeys = new();
                StringBuilder dataInsertion = new();

            using SQLiteConnection connection = new($"Data Source={dbFilePath};");
            connection.Open();

            List<string> tableNames = new();
            using (var cmd = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';", connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    tableNames.Add(reader.GetString(0));
                }
            }

            foreach (string tableName in tableNames)
            {
                // Get table creation SQL
                using (var cmd = new SQLiteCommand($"SELECT sql FROM sqlite_master WHERE type='table' AND name='{tableName}';", connection))
                {
                    string createTableSql = cmd.ExecuteScalar() as string;
                    tableCreation.AppendLine(createTableSql + ";");

                    // Extract foreign key definitions
                    string[] lines = createTableSql.Split('\n');
                    foreach (string line in lines)
                    {
                        if (line.Trim().StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
                        {
                            foreignKeys.AppendLine($"ALTER TABLE {tableName} ADD {line.Trim()};");
                        }
                    }
                }

                // Get column names and types
                List<(string Name, string Type)> columns = new();
                using (var cmd = new SQLiteCommand($"PRAGMA table_info('{tableName}');", connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        columns.Add((reader.GetString(1), reader.GetString(2))); // Name at index 1, Type at index 2
                    }
                }

                // Dump table data
                using (var cmd = new SQLiteCommand($"SELECT * FROM '{tableName}';", connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        StringBuilder insertSql = new();
                        if (!isExist) insertSql = new($"INSERT INTO `{tableName}` (" );
                        if (isExist) insertSql = new($"INSERT IGNORE INTO `{tableName}` (" );
                        insertSql.Append(string.Join(", ", columns.Select(c => c.Name)));
                        insertSql.Append(") VALUES (");

                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            if (i > 0) insertSql.Append(", ");
                            if (reader.IsDBNull(i))
                            {
                                insertSql.Append("NULL");
                            }
                            else if (columns[i].Type.ToUpper() == "BLOB")
                            {
                                byte[] blobData = (byte[])reader.GetValue(i);
                                string hexString = BitConverter.ToString(blobData).Replace("-", "");
                                insertSql.Append($"X'{hexString}'");
                            }
                            else
                            {
                                string value = reader.GetValue(i).ToString().Replace("'", "''");
                                insertSql.Append($"'{value}'");
                            }
                        }
                        insertSql.Append(");");
                        dataInsertion.AppendLine(insertSql.ToString());
                    }
                }
            }

            // Get and append index creation statements
            StringBuilder indexCreation = new();
            using (var cmd = new SQLiteCommand("SELECT sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL;", connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    indexCreation.AppendLine(reader.GetString(0) + ";");
                }
            }

            // Combine all parts in the correct order
            StringBuilder finalDump = new();
            finalDump.AppendLine("-- Script to Initialize a new database file\r\n");
            finalDump.AppendLine("SET FOREIGN_KEY_CHECKS=0;");
            if (!isExist)
            {
                finalDump.Append(tableCreation);
                //finalDump.Append(foreignKeys);
                finalDump.Append(indexCreation);
            }
            finalDump.Append(dataInsertion);
            finalDump.Replace("\"", "");
            finalDump.Replace("`", "");
            finalDump.Replace("INTEGER", "BIGINT");
            finalDump.Replace("TEXT", "VARCHAR(50)");
            finalDump.Replace("BLOB", "BLOB(1000)");
            return finalDump.ToString();
        }
        public static void ExecuteSqlScript(MySqlConnection connection, string script)
        {
            // Split on semicolons, remove empty statements, and trim whitespace
            var statements = script.Split(';')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s));

            foreach (var statement in statements)
            {
                using var cmd = new MySqlCommand(statement, connection);
                cmd.ExecuteNonQuery();
            }
        }

        // Guild-related methods added to match SQLiteDBManager functionality.

        public bool LoadGuilds(List<DBGuild> outGuilds)
        {
            try
            {
                using MySqlConnection connection = GetConnection();

                IEnumerable<DBGuild> guildQueryResult = connection.Query<DBGuild>("SELECT * FROM Guild");
                IEnumerable<DBGuildMember> memberQueryResult = connection.Query<DBGuildMember>("SELECT * FROM GuildMember");

                outGuilds.AddRange(guildQueryResult);

                // Build lookup
                Dictionary<long, DBGuild> guildLookup = new(outGuilds.Count);
                foreach (DBGuild guild in outGuilds)
                    guildLookup.Add(guild.Id, guild);

                foreach (DBGuildMember member in memberQueryResult)
                {
                    if (guildLookup.TryGetValue(member.GuildId, out DBGuild guild) == false)
                    {
                        Logger.Warn($"LoadGuilds(): Found orphan member [{member}]");
                        continue;
                    }

                    guild.Members.Add(member);
                }

                return true;
            }
            catch (Exception e)
            {
                outGuilds.Clear();
                Logger.ErrorException(e, nameof(LoadGuilds));
                return false;
            }
        }

        public bool SaveGuild(DBGuild guild)
        {
            try
            {
                using MySqlConnection connection = GetConnection();

                int inserted = connection.Execute("INSERT IGNORE INTO Guild (Id, Name, Motd, CreatorDbGuid, CreationTime) VALUES (@Id, @Name, @Motd, @CreatorDbGuid, @CreationTime)", guild);

                // Only name and MOTD should be mutable after creation.
                if (inserted == 0)
                    connection.Execute("UPDATE Guild SET Name=@Name, Motd=@Motd WHERE Id=@Id", guild);

                Logger.Trace($"SaveGuild(): {guild}");
                return true;
            }
            catch (Exception e)
            {
                Logger.ErrorException(e, nameof(SaveGuild));
                return false;
            }
        }

        public bool DeleteGuild(DBGuild guild)
        {
            using MySqlConnection connection = GetConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();

            try
            {
                connection.Execute("DELETE FROM GuildMember WHERE GuildId = @Id", guild, transaction);
                connection.Execute("DELETE FROM Guild WHERE Id = @Id", guild, transaction);

                transaction.Commit();

                Logger.Trace($"DeleteGuild(): {guild}");
                return true;
            }
            catch (Exception e)
            {
                transaction.Rollback();
                Logger.ErrorException(e, nameof(DeleteGuild));
                return false;
            }
        }

        public bool SaveGuildMember(DBGuildMember guildMember)
        {
            try
            {
                using MySqlConnection connection = GetConnection();

                int inserted = connection.Execute("INSERT IGNORE INTO GuildMember (PlayerDbGuid, GuildId, Membership) VALUES (@PlayerDbGuid, @GuildId, @Membership)", guildMember);

                // Only membership should be mutable after creation.
                if (inserted == 0)
                    connection.Execute("UPDATE GuildMember SET Membership=@Membership WHERE PlayerDbGuid=@PlayerDbGuid", guildMember);

                Logger.Trace($"SaveGuildMember(): {guildMember}");
                return true;
            }
            catch (Exception e)
            {
                Logger.ErrorException(e, nameof(SaveGuildMember));
                return false;
            }
        }

        public bool DeleteGuildMember(DBGuildMember guildMember)
        {
            try
            {
                using MySqlConnection connection = GetConnection();

                connection.Execute("DELETE FROM GuildMember WHERE PlayerDbGuid = @PlayerDbGuid", guildMember);

                Logger.Trace($"DeleteGuildMember(): {guildMember}");
                return true;
            }
            catch (Exception e)
            {
                Logger.ErrorException(e, nameof(DeleteGuildMember));
                return false;
            }
        }

        // MySQL backup using mysqldump (requires mysqldump to be on PATH or config.PathToMySqlDump set).
        // This is a pragmatic implementation: when CreateBackup runs it will call mysqldump using configured credentials
        // and store a timestamped SQL dump file in the data directory.
        private void CreateBackup()
        {
            //try
            //{
            //    Logger.Info("MySQLDBManager.CreateBackup(): Starting MySQL dump backup...");

            //    var config = ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>();
            //    string dumpExe = string.IsNullOrWhiteSpace(config.PathToMySqlDump) ? "mysqldump" : config.PathToMySqlDump;
            //    string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            //    if (!Directory.Exists(FileHelper.DataDirectory))
            //        Directory.CreateDirectory(FileHelper.DataDirectory);
            //    string backupFile = Path.Combine(FileHelper.DataDirectory, $"mysqldump_{config.MySqlDBName}_{timestamp}.sql");

            //    // Build argument list. Use --single-transaction for consistent dump of InnoDB.
            //    string args = $"--host={config.MySqlIP} --port={config.MySqlPort} --user={config}.MySqlUsername} --password={config.MySqlPw} --single-transaction --routines --triggers {config.MySqlDBName}";

            //    var psi = new ProcessStartInfo
            //    {
            //        FileName = dumpExe,
            //        Arguments = args,
            //        RedirectStandardOutput = true,
            //        RedirectStandardError = true,
            //        UseShellExecute = false,
            //        CreateNoWindow = true
            //    };

            //    using var proc = Process.Start(psi);
            //    if (proc == null)
            //    {
            //        Logger.Warn("CreateBackup(): Failed to start mysqldump process.");
            //        return;
            //    }

            //    // Read output and write to file
            //    using (var fs = new FileStream(backupFile, FileMode.Create, FileAccess.Write, FileShare.None))
            //    using (var sw = new StreamWriter(fs, Encoding.UTF8))
            //    {
            //        string stdout = proc.StandardOutput.ReadToEnd();
            //        sw.Write(stdout);
            //    }

            //    string stderr = proc.StandardError.ReadToEnd();
            //    proc.WaitForExit();

            //    if (proc.ExitCode != 0)
            //    {
            //        Logger.Warn($"CreateBackup(): mysqldump exited with code {proc.ExitCode}. stderr: {stderr}");
            //    }
            //    else
            //    {
            //        Logger.Info($"CreateBackup(): Created MySQL dump at {backupFile}");
            //        // rotate backups
            //        FileHelper.PrepareFileBackup(backupFile, _maxBackupNumber, out _);
            //    }
            //}
            //catch (Exception e)
            //{
            //    Logger.Warn($"CreateBackup(): error while attempting to perform DB backup: {e.Message}");
            //}
            //finally
            //{
            //    _backupInProgress = false;
            //}
        }
    }
}
