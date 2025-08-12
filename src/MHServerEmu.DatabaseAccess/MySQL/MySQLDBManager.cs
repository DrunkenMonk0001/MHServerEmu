using Dapper;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.DatabaseAccess.MySqlDB;
using MySql.Data.MySqlClient;
using MHServerEmu.Core.Helpers;
using MHServerEmu.DatabaseAccess.SQLite;
using System.Data.SQLite;
using System.Text;


namespace MHServerEmu.DatabaseAccess.MySQL
{
    /// <summary>
    /// Provides functionality for storing <see cref="DBAccount"/> instances in a MySql database using the <see cref="IDBManager"/> interface.
    /// </summary>
    public class MySQLDBManager : IDBManager
    {
        private const int CurrentSchemaVersion = 4;         // Increment this when making changes to the database schema
        private const int NumTestAccounts = 5;              // Number of test accounts to create for new databases
        private const int NumPlayerDataWriteAttempts = 3;   // Number of write attempts to do when saving player data
        public bool isSchemaExist;

        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly object _writeLock = new();

        private string _dbFilePath;
        private string _connectionString;

        public static MySQLDBManager Instance { get; } = new();
        private MySQLDBManager() { }

        public bool Initialize()
        {
            var config = ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>();
            _connectionString = $"Data Source={_dbFilePath}";
            _dbFilePath = Path.Combine(FileHelper.DataDirectory, ConfigManager.Instance.GetConfig<SQLiteDBManagerConfig>().FileName);
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

                return true;
            }
        }


        public bool TryQueryAccountByEmail(string email, out DBAccount account)
        {
            using MySqlConnection connection = GetConnection();
            var accounts = connection.Query<DBAccount>("SELECT * FROM Account WHERE Email = @Email", new { Email = email });

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

        public bool QueryIsPlayerNameTaken(string playerName)
        {
            using MySqlConnection connection = GetConnection();

            // This check is now explicitly case insensitive using collation
            var results = connection.Query<string>("SELECT PlayerName FROM Account WHERE PlayerName = @PlayerName COLLATE utf8mb4_general_ci", new { PlayerName = playerName });
            return results.Any();
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

            var @params = new { DbGuid = account.Id };

            var players = connection.Query<DBPlayer>("SELECT * FROM Player WHERE DbGuid = @DbGuid", @params);
            account.Player = players.FirstOrDefault();

            if (account.Player == null)
            {
                account.Player = new(account.Id);
                Logger.Info($"Initialized player data for account 0x{account.Id:X}");
            }

            account.Avatars.AddRange(LoadEntitiesFromTable(connection, "Avatar", account.Id));
            account.TeamUps.AddRange(LoadEntitiesFromTable(connection, "TeamUp", account.Id));
            account.Items.AddRange(LoadEntitiesFromTable(connection, "Item", account.Id));

            foreach (DBEntity avatar in account.Avatars)
            {
                account.Items.AddRange(LoadEntitiesFromTable(connection, "Item", avatar.DbGuid));
                account.ControlledEntities.AddRange(LoadEntitiesFromTable(connection, "ControlledEntity", avatar.DbGuid));
            }

            foreach (DBEntity teamUp in account.TeamUps)
            {
                account.Items.AddRange(LoadEntitiesFromTable(connection, "Item", teamUp.DbGuid));
            }

            return true;
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
            string connectionString = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(connectionStringVars).ToString();
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
            string connectionString = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(connectionStringVars).ToString();
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
                    MySqlScript importScript = new(connection, File.ReadAllText(filePath));
                    importScript.Execute();
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
                    MySqlScript importScript = new(connection, File.ReadAllText(filePath));
                    importScript.Execute();
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
                        connection.Execute(@"INSERT INTO Player (DbGuid) VALUES (@DbGuid) ON DUPLICATE KEY UPDATE DbGuid=@DbGuid", account.Player, transaction);
                        connection.Execute(@"UPDATE Player SET ArchiveData=@ArchiveData, StartTarget=@StartTarget,
                                    StartTargetRegionOverride=@StartTargetRegionOverride, AOIVolume=@AOIVolume, GazillioniteBalance=@GazillioniteBalance WHERE DbGuid = @DbGuid",
                                    account.Player, transaction);
                    }
                    else
                    {
                        Logger.Warn($"DoSavePlayerData(): Attempted to save null player entity data for account {account}");
                    }
                    UpdateEntityTable(connection, transaction, "Avatar", account.Id, account.Avatars);
                    UpdateEntityTable(connection, transaction, "TeamUp", account.Id, account.TeamUps);
                    UpdateEntityTable(connection, transaction, "Item", account.Id, account.Items);
                    foreach (DBEntity avatar in account.Avatars)
                    {
                        UpdateEntityTable(connection, transaction, "Item", avatar.DbGuid, account.Items);
                        UpdateEntityTable(connection, transaction, "ControlledEntity", avatar.DbGuid, account.ControlledEntities);
                    }
                    foreach (DBEntity teamUp in account.TeamUps)
                    {
                        UpdateEntityTable(connection, transaction, "Item", teamUp.DbGuid, account.Items);
                    }
                    connection.Execute("SET FOREIGN_KEY_CHECKS=1;", transaction: transaction);
                    transaction.Commit();
                    return true;
                }
                catch (Exception e)
                {
                    Logger.Warn($"DoSavePlayerData(): MySQL error for account [{account}]: {e.Message}");
                    transaction.Rollback();
                    return false;
                }
            }
        }

        /// Returns the user_version value of the current database.
        private static int GetSchemaVersion(MySqlConnection connection)
        {

            var queryResult = connection.Query<int>("SELECT * FROM " + ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>().MySqlDBName + ".schemaversion"); 
            if (queryResult.Any())
                return queryResult.Last();

            return Logger.WarnReturn(-1, "GetSchemaVersion(): Failed to query user_version from the DB");
        }


        /// Sets the user_version value of the current database.

        private static void SetSchemaVersion(MySqlConnection connection, int version)
        {
            connection.Execute($"UPDATE "+ ConfigManager.Instance.GetConfig<MySqlDBManagerConfig>().MySqlDBName + ".schemaversion SET schema_version="+CurrentSchemaVersion);
        }


        /// Loads <see cref="DBEntity"/> instances belonging to the specified container from the specified table.

        private static IEnumerable<DBEntity> LoadEntitiesFromTable(MySqlConnection connection, string tableName, long containerDbGuid)
        {
            var @params = new { ContainerDbGuid = containerDbGuid };
            return connection.Query<DBEntity>($"SELECT * FROM {tableName} WHERE ContainerDbGuid = @ContainerDbGuid", @params);
        }


        /// Updates <see cref="DBEntity"/> instances belonging to the specified container in the specified table using the provided <see cref="DBEntityCollection"/>.

        private static void UpdateEntityTable(MySqlConnection connection, MySqlTransaction transaction, string tableName,
            long containerDbGuid, DBEntityCollection dbEntityCollection)
        {
            var @params = new { ContainerDbGuid = containerDbGuid };

            // Delete items that no longer belong to this account
            var storedEntities = connection.Query<long>($"SELECT DbGuid FROM {tableName} WHERE ContainerDbGuid = @ContainerDbGuid", @params);
            var entitiesToDelete = storedEntities.Except(dbEntityCollection.Guids);
            if (entitiesToDelete.Any()) connection.Execute($"DELETE FROM {tableName} WHERE DbGuid IN ({string.Join(',', entitiesToDelete)})");

            // Insert and update
            IEnumerable<DBEntity> entries = dbEntityCollection.GetEntriesForContainer(containerDbGuid);

            connection.Execute(@$"INSERT IGNORE INTO {tableName} (DbGuid) VALUES (@DbGuid)", entries, transaction);
            connection.Execute(@$"UPDATE {tableName} SET ContainerDbGuid=@ContainerDbGuid, InventoryProtoGuid=@InventoryProtoGuid,
                                Slot=@Slot, EntityProtoGuid=@EntityProtoGuid, ArchiveData=@ArchiveData WHERE DbGuid=@DbGuid",
                                entries, transaction);
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
                        if (!isExist) insertSql = new($"INSERT INTO `{tableName}` (");
                        if (isExist) insertSql = new($"INSERT IGNORE INTO `{tableName}` (");
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
    }
}
