using Dapper;
using MHServerEmu.Core.Memory;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Core.Logging;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.MySQL
{
    /// <summary>
    /// Represents an entity table in a MySQL database.
    /// </summary>
    public class MySQLEntityTable
    {
        private static readonly Dictionary<DBEntityCategory, MySQLEntityTable> TableDict = new();

        private readonly string _selectAllQuery;
        private readonly string _selectIdsQuery;
        private readonly string _deleteQuery;
        private readonly string _insertQuery;
        private readonly string _updateQuery;

        public DBEntityCategory Category { get; }

        private MySQLEntityTable(DBEntityCategory category)
        {
            Category = category;

            _selectAllQuery = @$"SELECT * FROM {category} WHERE ContainerDbGuid = @ContainerDbGuid";
            _selectIdsQuery = @$"SELECT DbGuid FROM {category} WHERE ContainerDbGuid = @ContainerDbGuid";
            _deleteQuery    = @$"DELETE FROM {category} WHERE DbGuid IN @EntitiesToDelete";
            _insertQuery    = @$"INSERT IGNORE INTO {category} (DbGuid) VALUES (@DbGuid)";
            _updateQuery    = @$"UPDATE {category} SET ContainerDbGuid=@ContainerDbGuid, InventoryProtoGuid=@InventoryProtoGuid,
                                 Slot=@Slot, EntityProtoGuid=@EntityProtoGuid, ArchiveData=@ArchiveData WHERE DbGuid=@DbGuid";
        }

        public override string ToString()
        {
            return Category.ToString();
        }

        /// <summary>
        /// Returns the <see cref="MySQLEntityTable"/> instance for the specified <see cref="DBEntityCategory"/>.
        /// </summary>
        public static MySQLEntityTable GetTable(DBEntityCategory category)
        {
            if (TableDict.TryGetValue(category, out MySQLEntityTable table) == false)
            {
                table = new(category);
                TableDict.Add(category, table);
            }

            return table;
        }

        /// <summary>
        /// Loads <see cref="DBEntity"/> instances belonging to the specified container from this <see cref="MySQLEntityTable"/>
        /// and adds them to the provided <see cref="DBEntityCollection"/>.
        /// </summary>
        public void LoadEntities(MySqlConnection connection, long containerDbGuid, DBEntityCollection dbEntityCollection)
        {
            IEnumerable<DBEntity> entities = connection.Query<DBEntity>(_selectAllQuery, new { ContainerDbGuid = containerDbGuid });
            dbEntityCollection.AddRange(entities);          
        }

        /// <summary>
        /// Updates <see cref="DBEntity"/> instances belonging to the specified container in this <see cref="MySQLEntityTable"/> from the provided <see cref="DBEntityCollection"/>.
        /// </summary>
        public void UpdateEntities(MySqlConnection connection, MySqlTransaction transaction, long containerDbGuid, DBEntityCollection dbEntityCollection)
        {
            List<long> entitiesToDelete = ListPool<long>.Instance.Get();
            GetEntitiesToDelete(connection, containerDbGuid, dbEntityCollection, entitiesToDelete, transaction);

            try
            {
                if (entitiesToDelete.Count > 0)
                {
                    var sql = $"DELETE FROM {Category} WHERE DbGuid IN ({string.Join(",", entitiesToDelete.Select((_, i) => "@id" + i))})";
                    var parameters = new Dapper.DynamicParameters();
                    for (int i = 0; i < entitiesToDelete.Count; i++)
                        parameters.Add($"id{i}", entitiesToDelete[i]);
                    connection.Execute(sql, parameters, transaction);
                }
            }
            finally
            {
                ListPool<long>.Instance.Return(entitiesToDelete);
            }

            var dirtyEntries = dbEntityCollection.GetEntriesForContainer(containerDbGuid).Where(e => e.IsDirty).ToList();
            if (dirtyEntries.Count > 0)
            {
                connection.Execute($"INSERT IGNORE INTO {Category} (DbGuid) VALUES (@DbGuid)", dirtyEntries, transaction);
                connection.Execute($"UPDATE {Category} SET ContainerDbGuid=@ContainerDbGuid, InventoryProtoGuid=@InventoryProtoGuid, Slot=@Slot, EntityProtoGuid=@EntityProtoGuid, ArchiveData=@ArchiveData WHERE DbGuid=@DbGuid", dirtyEntries, transaction);
                foreach (var entity in dirtyEntries)
                    entity.IsDirty = false;
            }
        }

        /// <summary>
        /// Queries ids of entities that no longer belong to the specified container and adds them to the provided <see cref="List{T}"/>.
        /// </summary>
        private void GetEntitiesToDelete(MySqlConnection connection, long containerDbGuid, DBEntityCollection dbEntityCollection, List<long> entitiesToDelete, MySqlTransaction transaction)
        {
            IEnumerable<long> storedDbGuids = connection.Query<long>(_selectIdsQuery, new { ContainerDbGuid = containerDbGuid }, transaction);
            if (storedDbGuids is IReadOnlyList<long> list)
            {
                // Access elements by index in indexable collections to avoid allocating IEnumerator instances.
                int count = list.Count;
                for (int i = 0; i < count; i++)
                {
                    long storedDbGuid = list[i];
                    if (dbEntityCollection.Contains(storedDbGuid) == false)
                        entitiesToDelete.Add(storedDbGuid);
                }
            }
            else
            {
                // Fall back to foreach for non-indexable collections.
                foreach (long storedDbGuid in storedDbGuids)
                {
                    if (dbEntityCollection.Contains(storedDbGuid) == false)
                        entitiesToDelete.Add(storedDbGuid);
                }
            }
        }
    }
}
