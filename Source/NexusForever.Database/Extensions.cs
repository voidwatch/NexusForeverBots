using Microsoft.EntityFrameworkCore;
using System;
using NexusForever.Database.Configuration;

namespace NexusForever.Database
{
    public static class Extensions
    {
        public static DbContextOptionsBuilder UseConfiguration(this DbContextOptionsBuilder optionsBuilder, IDatabaseConfig databaseConfiguration, DatabaseType databaseType)
        {
            var connectionString = databaseConfiguration.GetConnectionString(databaseType);
            switch (connectionString.Provider)
            {
                case DatabaseProvider.MySql:
                    optionsBuilder.UseMySql(connectionString.ConnectionString, ServerVersion.AutoDetect(connectionString.ConnectionString), b =>
                    {
                        b.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
                    });
                    break;
                default:
                    throw new NotSupportedException($"The requested database provider: {connectionString.Provider:G} is not supported.");
            }
            return optionsBuilder;
        }

        /// <summary>
        /// A key column mapped with HasDefaultValue counts as generated on insert, so EF gives a key value of 0 (objective index 0, action set 0 ...)
        /// a temporary value instead of sending it. Temporary values come from one counter shared by all contexts, and when it reaches a real
        /// value of the same row family (quest 9586: index 0 got the temporary value 3 while objective 3 was added) the add throws
        /// "another instance with the same key value is already being tracked" and the whole character batch is lost.
        /// The columns keep their database default; EF now always sends the value. Used by the auth, character and world contexts.
        /// </summary>
        public static void KeepKeyValues(this ModelBuilder modelBuilder)
        {
            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
            {
                Microsoft.EntityFrameworkCore.Metadata.IMutableKey key = entityType.FindPrimaryKey();
                if (key == null)
                    continue;

                foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property in key.Properties)
                    if (property.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd
                        && (property.GetDefaultValue() != null || property.GetDefaultValueSql() != null))
                        property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }
}
