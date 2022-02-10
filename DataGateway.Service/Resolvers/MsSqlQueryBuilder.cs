using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using Azure.DataGateway.Service.Models;
using Microsoft.Data.SqlClient;

namespace Azure.DataGateway.Service.Resolvers
{
    /// <summary>
    /// Class for building MsSql queries.
    /// </summary>
    public class MsSqlQueryBuilder : BaseSqlQueryBuilder, IQueryBuilder
    {
        private const string FOR_JSON_SUFFIX = " FOR JSON PATH, INCLUDE_NULL_VALUES";
        private const string WITHOUT_ARRAY_WRAPPER_SUFFIX = "WITHOUT_ARRAY_WRAPPER";

        private static DbCommandBuilder _builder = new SqlCommandBuilder();

        /// <inheritdoc />
        protected override string QuoteIdentifier(string ident)
        {
            return _builder.QuoteIdentifier(ident);
        }

        /// <inheritdoc />
        public string Build(SqlQueryStructure structure)
        {
            string dataIdent = QuoteIdentifier(SqlQueryStructure.DATA_IDENT);
            string fromSql = $"{QuoteIdentifier(structure.TableName)} AS {QuoteIdentifier(structure.TableAlias)}{Build(structure.Joins)}";
            ;

            fromSql += string.Join(
                    "",
                    structure.JoinQueries.Select(
                        x => $" OUTER APPLY ({Build(x.Value)}) AS {QuoteIdentifier(x.Key)}({dataIdent})"));

            string predicates = JoinPredicateStrings(
                                    Build(structure.Predicates),
                                    Build(structure.PaginationMetadata.PaginationPredicate));

            string query = $"SELECT TOP {structure.Limit()} {WrappedColumns(structure)}"
                + $" FROM {fromSql}"
                + $" WHERE {predicates}"
                + $" ORDER BY {Build(structure.PrimaryKeyAsColumns())}";

            query += FOR_JSON_SUFFIX;
            if (!structure.IsListQuery)
            {
                query += "," + WITHOUT_ARRAY_WRAPPER_SUFFIX;
            }

            return query;
        }

        /// <inheritdoc />
        public string Build(SqlInsertStructure structure)
        {

            return $"INSERT INTO {QuoteIdentifier(structure.TableName)} ({Build(structure.InsertColumns)}) " +
                    $"OUTPUT {MakeOutputColumns(structure.ReturnColumns, OutputQualifier.Inserted)} " +
                    $"VALUES ({string.Join(", ", structure.Values)});";
        }

        /// <inheritdoc />
        public string Build(SqlUpdateStructure structure)
        {
            return $"UPDATE {QuoteIdentifier(structure.TableName)} " +
                    $"SET {Build(structure.UpdateOperations, ", ")} " +
                    $"OUTPUT {MakeOutputColumns(structure.ReturnColumns, OutputQualifier.Inserted)} " +
                    $"WHERE {Build(structure.Predicates)};";
        }

        /// <inheritdoc />
        public string Build(SqlDeleteStructure structure)
        {
            return $"DELETE FROM {QuoteIdentifier(structure.TableName)} " +
                    $"WHERE {Build(structure.Predicates)} ";
        }

        /// <summary>
        /// Avoid redundant check, wrap the sequence in a transaction,
        /// and protect the first table access with appropriate locking.
        /// </summary>
        /// <param name="structure"></param>
        /// <returns></returns>
        public string Build(SqlUpsertQueryStructure structure)
        {
            return $"SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;BEGIN TRANSACTION; UPDATE { QuoteIdentifier(structure.TableName)} " +
                $"WITH(UPDLOCK) SET {Build(structure.UpdateOperations, ", ")} " +
                $"OUTPUT {MakeOutputColumns(structure.ReturnColumns, OutputQualifier.Inserted)} " +
                $"WHERE {Build(structure.Predicates)} " +
                $"IF @@ROWCOUNT = 0 " +
                $"BEGIN; " +
                $"INSERT INTO {QuoteIdentifier(structure.TableName)} ({Build(structure.InsertColumns)}) " +
                $"OUTPUT {MakeOutputColumns(structure.ReturnColumns, OutputQualifier.Inserted)} " +
                $"VALUES ({string.Join(", ", structure.Values)}) " +
                $"END; COMMIT TRANSACTION";
        }

        /// <summary>
        /// Labels with which columns can be marked in the OUTPUT clause
        /// </summary>
        private enum OutputQualifier { Inserted, Deleted };

        /// <summary>
        /// Adds qualifiers (inserted or deleted) to columns in OUTPUT clause and joins them with commas.
        /// e.g. for columns [C1, C2, C3] and output qualifier Inserted
        /// return Inserted.C1, Inserted.C2, Inserted.C3
        /// </summary>
        private string MakeOutputColumns(List<string> columns, OutputQualifier outputQualifier)
        {
            List<string> outputColumns = columns.Select(column => $"{outputQualifier}.{QuoteIdentifier(column)}").ToList();
            return string.Join(", ", outputColumns);
        }

        /// <inheritdoc />
        protected override string Build(KeysetPaginationPredicate predicate)
        {
            if (predicate == null)
            {
                return string.Empty;
            }

            if (predicate.PrimaryKey.Count > 1)
            {
                StringBuilder result = new("(");
                for (int i = 0; i < predicate.PrimaryKey.Count; i++)
                {
                    if (i > 0)
                    {
                        result.Append(" OR ");
                    }

                    result.Append($"({MakePaginationInequality(predicate.PrimaryKey, predicate.Values, i)})");
                }

                result.Append(")");

                return result.ToString();
            }
            else
            {
                return MakePaginationInequality(predicate.PrimaryKey, predicate.Values, 0);
            }
        }

        /// <summary>
        /// Create an inequality where all primary key columns up to untilIndex are equilized to the
        /// respective pkValue, and the primary key colum at untilIndex has to be greater than its pkValue
        /// E.g. for
        /// primaryKey: [a, b, c, d, e, f]
        /// pkValues: [A, B, C, D, E, F]
        /// untilIndex: 2
        /// generate <c>a = A AND b = B AND c > C</c>
        /// </summary>
        private string MakePaginationInequality(List<Column> primaryKey, List<string> pkValues, int untilIndex)
        {
            StringBuilder result = new();
            for (int i = 0; i <= untilIndex; i++)
            {
                string op = i == untilIndex ? ">" : "=";
                result.Append($"{Build(primaryKey[i])} {op} {pkValues[i]}");

                if (i < untilIndex)
                {
                    result.Append(" AND ");
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Add a JSON_QUERY wrapper on the column
        /// </summary>
        private string WrapSubqueryColumn(LabelledColumn column, SqlQueryStructure subquery)
        {
            string builtColumn = Build(column as Column);
            if (subquery.IsListQuery)
            {
                return $"JSON_QUERY (COALESCE({builtColumn}, '[]'))";
            }

            return $"JSON_QUERY ({builtColumn})";
        }

        /// <summary>
        /// Build columns and wrap columns which represent join subqueries
        /// </summary>
        private string WrappedColumns(SqlQueryStructure structure)
        {
            Func<LabelledColumn, bool> isJoinColumn = c => structure.JoinQueries.ContainsKey(c.TableAlias);
            Func<LabelledColumn, SqlQueryStructure> getJoinSubquery = c => structure.JoinQueries[c.TableAlias];

            return string.Join(", ",
                structure.Columns.Select(
                    c => isJoinColumn(c) ?
                        WrapSubqueryColumn(c, getJoinSubquery(c)) + $" AS {QuoteIdentifier(c.Label)}" :
                        Build(c)
            ));
        }
    }
}
