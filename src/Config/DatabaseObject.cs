namespace Azure.DataApiBuilder.Config
{
    /// <summary>
    /// Represents a database object - which could be a view, table, or stored procedure.
    /// </summary>
    public class DatabaseObject
    {
        public string SchemaName { get; set; } = null!;

        public string Name { get; set; } = null!;

        public TableDefinition TableDefinition { get; set; } = null!;

        public StoredProcedureDefinition StoredProcedureDefinition { get; set; } = null!;

        public SourceType ObjectType { get; set; } = SourceType.Table;

        public DatabaseObject(string schemaName, string tableName)
        {
            SchemaName = schemaName;
            Name = tableName;
        }

        public DatabaseObject() { }

        public string FullName
        {
            get
            {
                return string.IsNullOrEmpty(SchemaName) ? Name : $"{SchemaName}.{Name}";
            }
        }

        public override bool Equals(object? other)
        {
            return Equals(other as DatabaseObject);
        }

        public bool Equals(DatabaseObject? other)
        {
            return other is not null &&
                   SchemaName.Equals(other.SchemaName) &&
                   Name.Equals(other.Name);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(SchemaName, Name);
        }
    }

    public class StoredProcedureDefinition
    {
        /// <summary>
        /// The list of input parameters
        /// Key: parameter name, Value: ParameterDefinition object
        /// </summary>
        public Dictionary<string, ParameterDefinition> Parameters { get; set; } = new();
    }

    public class ParameterDefinition
    {
        public Type SystemType { get; set; } = null!;
        public bool HasConfigDefault { get; set; }
        public object? ConfigDefaultValue { get; set; }
    }

    public class TableDefinition
    {
        /// <summary>
        /// Points to the actual source table in the database for the object,
        /// which is evaluated on a per request basis depending on the fields
        /// being modified by the mutation request.
        /// It is populated only for views and is null otherwise, since for
        /// tables, base table is same as the source table.
        /// </summary>
        public TableDefinition? BaseTableDefinition { get; set; }
        public Dictionary<string, TableDefinition> BaseTableDefinitions { get; set; } = new();
        /// <summary>
        /// The list of columns that together form the primary key of the table.
        /// </summary>
        public List<string> PrimaryKey { get; set; } = new();

        /// <summary>
        /// The list of columns in this table.
        /// </summary>
        public Dictionary<string, ColumnDefinition> Columns { get; private set; } =
            new(StringComparer.InvariantCultureIgnoreCase);

        public Dictionary<string, string> ColumnAliasesFromBaseTable { get; set; } = new();

        /// <summary>
        /// A dictionary mapping all the source entities to their relationship metadata.
        /// All these entities share this table definition
        /// as their underlying database object 
        /// </summary>
        public Dictionary<string, RelationshipMetadata> SourceEntityRelationshipMap { get; private set; } =
            new(StringComparer.InvariantCultureIgnoreCase);

        /// <summary>
        /// Given the list of column names to check, evaluates
        /// if any of them is a nullable column when matched with the columns in this table definition.
        /// </summary>
        /// <param name="columnsToCheck">List of column names.</param>
        /// <returns>True if any of the columns is null, false otherwise.</returns>
        public bool IsAnyColumnNullable(List<string> columnsToCheck)
        {
            // If any of the given columns are nullable, the relationship is nullable.
            return columnsToCheck.Select(column =>
                                         Columns.TryGetValue(column, out ColumnDefinition? definition) && definition.IsNullable)
                                 .Where(isNullable => isNullable == true)
                                 .Any();
        }
    }

    /// <summary>
    /// Class encapsulating foreign keys corresponding to target entities.
    /// </summary>
    public class RelationshipMetadata
    {
        /// <summary>
        /// Dictionary of target entity name to ForeignKeyDefinition.
        /// </summary>
        public Dictionary<string, List<ForeignKeyDefinition>> TargetEntityToFkDefinitionMap { get; private set; }
            = new(StringComparer.InvariantCultureIgnoreCase);
    }

    public class ColumnDefinition
    {
        /// <summary>
        /// The database type of this column mapped to the SystemType.
        /// </summary>
        public Type SystemType { get; set; } = typeof(object);
        public bool HasDefault { get; set; }
        public bool IsAutoGenerated { get; set; }
        public bool IsNullable { get; set; }
        public object? DefaultValue { get; set; }
    }

    public class ForeignKeyDefinition
    {
        /// <summary>
        /// The referencing and referenced table pair.
        /// </summary>
        public RelationShipPair Pair { get; set; } = new();

        /// <summary>
        /// The list of columns referenced in the reference table.
        /// If this list is empty, the primary key columns of the referenced
        /// table are implicitly assumed to be the referenced columns.
        /// </summary>
        public List<string> ReferencedColumns { get; set; } = new();

        /// <summary>
        /// The list of columns of the table that make up the foreign key.
        /// If this list is empty, the primary key columns of the referencing
        /// table are implicitly assumed to be the foreign key columns.
        /// </summary>
        public List<string> ReferencingColumns { get; set; } = new();

        public override bool Equals(object? other)
        {
            return Equals(other as ForeignKeyDefinition);
        }

        public bool Equals(ForeignKeyDefinition? other)
        {
            return other != null &&
                   Pair.Equals(other.Pair) &&
                   ReferencedColumns.SequenceEqual(other.ReferencedColumns) &&
                   ReferencingColumns.SequenceEqual(other.ReferencingColumns);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                    Pair, ReferencedColumns, ReferencingColumns);
        }
    }

    public class RelationShipPair
    {
        public RelationShipPair() { }

        public RelationShipPair(
            DatabaseObject referencingDbObject,
            DatabaseObject referencedDbObject)
        {
            ReferencingDbObject = referencingDbObject;
            ReferencedDbObject = referencedDbObject;
        }

        public DatabaseObject ReferencingDbObject { get; set; } = new();

        public DatabaseObject ReferencedDbObject { get; set; } = new();

        public override bool Equals(object? other)
        {
            return Equals(other as RelationShipPair);
        }

        public bool Equals(RelationShipPair? other)
        {
            return other != null &&
                   ReferencedDbObject.Equals(other.ReferencedDbObject) &&
                   ReferencingDbObject.Equals(other.ReferencingDbObject);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                    ReferencedDbObject, ReferencingDbObject);
        }
    }

    public class AuthorizationRule
    {
        /// <summary>
        /// The various type of AuthZ scenarios supported: Anonymous, Authenticated.
        /// </summary>
        public AuthorizationType AuthorizationType { get; set; }
    }
}
