using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.Models;
using Azure.DataApiBuilder.Service.Parsers;
using Azure.DataApiBuilder.Service.Services;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.OData.UriParser;

namespace Azure.DataApiBuilder.Service.Resolvers
{

    /// <summary>
    /// Holds shared properties and methods among
    /// Sql*QueryStructure classes
    /// </summary>
    public abstract class BaseSqlQueryStructure : BaseQueryStructure
    {
        protected ISqlMetadataProvider SqlMetadataProvider { get; }

        /// <summary>
        /// The Entity associated with this query.
        /// </summary>
        public string EntityName { get; protected set; }

        public string BaseEntityName { get; protected set; }
        public DatabaseObject DatabaseObjectForBaseEntity { get; }

        /// <summary>
        /// The DatabaseObject associated with the entity, represents the
        /// databse object to be queried.
        /// </summary>
        public DatabaseObject DatabaseObject { get; }

        /// <summary>
        /// The alias of the main table to be queried.
        /// </summary>
        public string TableAlias { get; protected set; }

        public Dictionary<string, string> ColumnAliases { get; protected set; } = new();

        /// <summary>
        /// FilterPredicates is a string that represents the filter portion of our query
        /// in the WHERE Clause. This is generated specifically from the $filter portion
        /// of the query string.
        /// </summary>
        public string? FilterPredicates { get; set; }

        /// <summary>
        /// DbPolicyPredicates is a string that represents the filter portion of our query
        /// in the WHERE Clause added by virtue of the database policy.
        /// </summary>
        public string? DbPolicyPredicates { get; set; }

        public BaseSqlQueryStructure(
            ISqlMetadataProvider sqlMetadataProvider,
            string entityName,
            IncrementingInteger? counter = null,
            string? baseEntityName =  null,
            Dictionary<string, string>? columnAliases = null)
            : base(counter)
        {
            SqlMetadataProvider = sqlMetadataProvider;
            if (!string.IsNullOrEmpty(entityName))
            {
                EntityName = entityName;
                DatabaseObject = sqlMetadataProvider.EntityToDatabaseObject[entityName];
            }
            else
            {
                EntityName = string.Empty;
                DatabaseObject = new();
            }

            BaseEntityName = baseEntityName is null ? entityName : baseEntityName;
            DatabaseObjectForBaseEntity = sqlMetadataProvider.EntityToDatabaseObject[BaseEntityName];
            // Default the alias to the empty string since this base construtor
            // is called for requests other than Find operations. We only use
            // TableAlias for Find, so we leave empty here and then populate
            // in the Find specific contructor.
            TableAlias = string.Empty;

            ColumnAliases = columnAliases is null ? new() : columnAliases;
        }

        /// <summary>
        /// For UPDATE (OVERWRITE) operation
        /// Adds result of (TableDefinition.Columns minus MutationFields) to UpdateOperations with null values
        /// There will not be any columns leftover that are PK, since they are handled in request validation.
        /// </summary>
        /// <param name="leftoverSchemaColumns"></param>
        /// <param name="updateOperations">List of Predicates representing UpdateOperations.</param>
        /// <param name="tableDefinition">The definition for the table.</param>
        public void AddNullifiedUnspecifiedFields(List<string> leftoverSchemaColumns, List<Predicate> updateOperations, TableDefinition tableDefinition)
        {
            //result of adding (TableDefinition.Columns - MutationFields) to UpdateOperations
            foreach (string leftoverColumn in leftoverSchemaColumns)
            {
                // If the left over column is autogenerated
                // then no need to add it with a null value.
                if (tableDefinition.Columns[leftoverColumn].IsAutoGenerated)
                {
                    continue;
                }

                else
                {
                    Predicate predicate = new(
                        new PredicateOperand(new Column(tableSchema: DatabaseObject.SchemaName, tableName: DatabaseObject.Name, leftoverColumn)),
                        PredicateOperation.Equal,
                        new PredicateOperand($"@{MakeParamWithValue(value: null)}")
                    );

                    updateOperations.Add(predicate);
                }
            }
        }

        /// <summary>
        /// Get column type from table underlying the query strucutre
        /// </summary>
        public Type GetColumnSystemType(string columnName)
        {
            if (GetUnderlyingTableDefinition().Columns.TryGetValue(columnName, out ColumnDefinition? column))
            {
                return column.SystemType;
            }
            else
            {
                throw new ArgumentException($"{columnName} is not a valid column of {DatabaseObject.Name}");
            }
        }

        /// <summary>
        /// Returns the TableDefinition for the the table of this query.
        /// </summary>
        protected TableDefinition GetUnderlyingTableDefinition()
        {
            return SqlMetadataProvider.GetTableDefinition(EntityName);
        }

        /// <summary>
        /// Return the StoredProcedureDefinition associated with this database object
        /// </summary>
        protected StoredProcedureDefinition GetUnderlyingStoredProcedureDefinition()
        {
            return SqlMetadataProvider.GetStoredProcedureDefinition(EntityName);
        }

        /// <summary>
        /// Get primary key as list of string
        /// </summary>
        public List<string> PrimaryKey()
        {
            return GetUnderlyingTableDefinition().PrimaryKey;
        }

        /// <summary>
        /// get all columns of the table
        /// </summary>
        public List<string> AllColumns()
        {
            return GetUnderlyingTableDefinition().Columns.Select(col => col.Key).ToList();
        }

        /// <summary>
        /// Get a list of the output columns for this table.
        /// An output column is a labelled column that holds
        /// both the backing column and a label with the exposed name.
        /// </summary>
        /// <returns>List of LabelledColumns</returns>
        protected List<LabelledColumn> GenerateOutputColumns()
        {
            List<LabelledColumn> outputColumns = new();
            TableDefinition baseTableDefinition = SqlMetadataProvider.GetTableDefinition(BaseEntityName);
            foreach (string columnName in baseTableDefinition.Columns.Keys)
            {
                string aliasName = ColumnAliases.ContainsKey(columnName) ?
                    ColumnAliases[columnName] : columnName;
                // if column is not exposed we skip
                if (!SqlMetadataProvider.TryGetExposedColumnName(
                    entityName: EntityName,
                    backingFieldName: aliasName,
                    out string? exposedName))
                {
                    continue;
                }

                outputColumns.Add(new(
                    tableSchema: DatabaseObject.SchemaName,
                    tableName: DatabaseObject.Name,
                    columnName: aliasName,
                    label: exposedName!,
                    tableAlias: TableAlias));
            }

            return outputColumns;
        }

        ///<summary>
        /// Gets the value of the parameter cast as the system type
        /// of the column this parameter is associated with
        ///</summary>
        /// <exception cref="ArgumentException">columnName is not a valid column of table or param
        /// does not have a valid value type</exception>
        protected object GetParamAsColumnSystemType(string param, string columnName)
        {
            Type systemType = GetColumnSystemType(columnName);
            try
            {
                return ParseParamAsSystemType(param, systemType);
            }
            catch (Exception e)
            {
                if (e is FormatException ||
                    e is ArgumentNullException ||
                    e is OverflowException)
                {
                    throw new ArgumentException($"Parameter \"{param}\" cannot be resolved as column \"{columnName}\" " +
                        $"with type \"{systemType.Name}\".");
                }

                throw;
            }
        }

        /// <summary>
        /// Tries to parse the string parameter to the given system type
        /// Useful for inferring parameter types for columns or procedure parameters
        /// </summary>
        /// <param name="param"></param>
        /// <param name="systemType"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        protected object ParseParamAsSystemType(string param, Type systemType)
        {
            return systemType.Name switch
            {
                "String" => param,
                "Byte" => byte.Parse(param),
                "Byte[]" => Convert.FromBase64String(param),
                "Int16" => short.Parse(param),
                "Int32" => int.Parse(param),
                "Int64" => long.Parse(param),
                "Single" => float.Parse(param),
                "Double" => double.Parse(param),
                "Decimal" => decimal.Parse(param),
                "Boolean" => bool.Parse(param),
                "DateTime" => DateTimeOffset.Parse(param),
                "Guid" => Guid.Parse(param),
                _ => throw new NotSupportedException($"{systemType.Name} is not supported")
            };
        }

        /// <summary>
        /// Very similar to GQLArgumentToDictParams but only extracts the argument names from
        /// the specified field which means that the method does not need a middleware context
        /// to resolve the values of the arguments
        /// </summary>
        /// <param name="fieldName">the field from which to extract the argument names</param>
        /// <param name="mutationParameters">a dictionary of mutation parameters</param>
        internal static List<string> GetSubArgumentNamesFromGQLMutArguments
        (
            string fieldName,
            IDictionary<string, object?> mutationParameters)
        {
            string errMsg;

            if (mutationParameters.TryGetValue(fieldName, out object? item))
            {
                // An inline argument was set
                // TODO: This assumes the input was NOT nullable.
                if (item is List<ObjectFieldNode> mutationInputRaw)
                {
                    return mutationInputRaw.Select(node => node.Name.Value).ToList();
                }
                else
                {
                    errMsg = $"Unexpected {fieldName} argument format.";
                }
            }
            else
            {
                errMsg = $"Expected {fieldName} argument in mutation arguments.";
            }

            // should not happen due to gql schema validation
            throw new DataApiBuilderException(
                message: errMsg,
                subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest,
                statusCode: HttpStatusCode.BadRequest);
        }

        /// <summary>
        /// Creates a dictionary of fields and their values
        /// from a field with type List<ObjectFieldNode> fetched
        /// a dictionary of parameters
        /// Used to extract values from parameters
        /// </summary>
        /// <param name="context">GQL middleware context used to resolve the values of arguments</param>
        /// <param name="fieldName">the gql field from which to extract the parameters</param>
        /// <param name="mutationParameters">a dictionary of mutation parameters</param>
        /// <exception cref="InvalidDataException"></exception>
        internal static IDictionary<string, object?> GQLMutArgumentToDictParams(
            IMiddlewareContext context,
            string fieldName,
            IDictionary<string, object?> mutationParameters)
        {
            string errMsg;

            if (mutationParameters.TryGetValue(fieldName, out object? item))
            {
                IObjectField fieldSchema = context.Selection.Field;
                IInputField itemsArgumentSchema = fieldSchema.Arguments[fieldName];
                InputObjectType itemsArgumentObject = ResolverMiddleware.InputObjectTypeFromIInputField(itemsArgumentSchema);

                Dictionary<string, object?> mutationInput;
                // An inline argument was set
                // TODO: This assumes the input was NOT nullable.
                if (item is List<ObjectFieldNode> mutationInputRaw)
                {
                    mutationInput = new Dictionary<string, object?>();
                    foreach (ObjectFieldNode node in mutationInputRaw)
                    {
                        string nodeName = node.Name.Value;
                        mutationInput.Add(nodeName, ResolverMiddleware.ExtractValueFromIValueNode(
                            value: node.Value,
                            argumentSchema: itemsArgumentObject.Fields[nodeName],
                            variables: context.Variables));
                    }

                    return mutationInput;
                }
                else
                {
                    errMsg = $"Unexpected {fieldName} argument format.";
                }
            }
            else
            {
                errMsg = $"Expected {fieldName} argument in mutation arguments.";
            }

            // should not happen due to gql schema validation
            throw new DataApiBuilderException(
                message: errMsg,
                subStatusCode: DataApiBuilderException.SubStatusCodes.BadRequest,
                statusCode: HttpStatusCode.BadRequest);
        }

        /// <summary>
        /// After SqlQueryStructure is instantiated, process a database authorization policy
        /// for GraphQL requests with the ODataASTVisitor to populate DbPolicyPredicates.
        /// Processing will also occur for GraphQL sub-queries.
        /// </summary>
        /// <param name="dbPolicyClause">FilterClause from processed runtime configuration permissions Policy:Database</param>
        /// <exception cref="DataApiBuilderException">Thrown when the OData visitor traversal fails. Possibly due to malformed clause.</exception>
        public void ProcessOdataClause(FilterClause odataClause)
        {
            ODataASTVisitor visitor = new(this, this.SqlMetadataProvider);
            try
            {
                DbPolicyPredicates = GetFilterPredicatesFromOdataClause(odataClause, visitor);
            }
            catch
            {
                throw new DataApiBuilderException(message: "Policy query parameter is not well formed for GraphQL Policy Processing.",
                                               statusCode: HttpStatusCode.Forbidden,
                                               subStatusCode: DataApiBuilderException.SubStatusCodes.AuthorizationCheckFailed);
            }
        }

        protected static string? GetFilterPredicatesFromOdataClause(FilterClause filterClause, ODataASTVisitor visitor)
        {
            return filterClause.Expression.Accept<string>(visitor);
        }
    }
}
