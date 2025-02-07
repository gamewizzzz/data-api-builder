// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Azure.DataApiBuilder.Config.DatabasePrimitives;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.GraphQLBuilder.CustomScalars;
using Azure.DataApiBuilder.Service.GraphQLBuilder.Directives;
using Azure.DataApiBuilder.Service.GraphQLBuilder.Queries;
using HotChocolate.Language;
using HotChocolate.Types;
using HotChocolate.Types.NodaTime;
using static Azure.DataApiBuilder.Service.GraphQLBuilder.GraphQLNaming;
using static Azure.DataApiBuilder.Service.GraphQLBuilder.GraphQLStoredProcedureBuilder;
using static Azure.DataApiBuilder.Service.GraphQLBuilder.GraphQLTypes.SupportedHotChocolateTypes;

namespace Azure.DataApiBuilder.Service.GraphQLBuilder.Sql
{
    public static class SchemaConverter
    {
        /// <summary>
        /// Generate a GraphQL object type from a SQL table/view/stored-procedure definition, combined with the runtime config entity information
        /// </summary>
        /// <param name="entityName">Name of the entity in the runtime config to generate the GraphQL object type for.</param>
        /// <param name="databaseObject">SQL database object information.</param>
        /// <param name="configEntity">Runtime config information for the table.</param>
        /// <param name="entities">Key/Value Collection mapping entity name to the entity object,
        /// currently used to lookup relationship metadata.</param>
        /// <param name="rolesAllowedForEntity">Roles to add to authorize directive at the object level (applies to query/read ops).</param>
        /// <param name="rolesAllowedForFields">Roles to add to authorize directive at the field level (applies to mutations).</param>
        /// <returns>A GraphQL object type to be provided to a Hot Chocolate GraphQL document.</returns>
        public static ObjectTypeDefinitionNode FromDatabaseObject(
            string entityName,
            DatabaseObject databaseObject,
            [NotNull] Entity configEntity,
            RuntimeEntities entities,
            IEnumerable<string> rolesAllowedForEntity,
            IDictionary<string, IEnumerable<string>> rolesAllowedForFields)
        {
            Dictionary<string, FieldDefinitionNode> fields = new();
            List<DirectiveNode> objectTypeDirectives = new();
            SourceDefinition sourceDefinition = databaseObject.SourceDefinition;
            NameNode nameNode = new(value: GetDefinedSingularName(entityName, configEntity));

            // When the result set is not defined, it could be a mutation operation with no returning columns
            // Here we create a field called result which will be an empty array.
            if (databaseObject.SourceType is EntitySourceType.StoredProcedure && ((StoredProcedureDefinition)sourceDefinition).Columns.Count == 0)
            {
                FieldDefinitionNode field = GetDefaultResultFieldForStoredProcedure();

                fields.TryAdd("result", field);
            }

            foreach ((string columnName, ColumnDefinition column) in sourceDefinition.Columns)
            {
                List<DirectiveNode> directives = new();

                if (databaseObject.SourceType is not EntitySourceType.StoredProcedure && sourceDefinition.PrimaryKey.Contains(columnName))
                {
                    directives.Add(new DirectiveNode(PrimaryKeyDirectiveType.DirectiveName, new ArgumentNode("databaseType", column.SystemType.Name)));
                }

                if (databaseObject.SourceType is not EntitySourceType.StoredProcedure && column.IsReadOnly)
                {
                    directives.Add(new DirectiveNode(AutoGeneratedDirectiveType.DirectiveName));
                }

                if (databaseObject.SourceType is not EntitySourceType.StoredProcedure && column.DefaultValue is not null)
                {
                    IValueNode arg = CreateValueNodeFromDbObjectMetadata(column.DefaultValue);

                    directives.Add(new DirectiveNode(DefaultValueDirectiveType.DirectiveName, new ArgumentNode("value", arg)));
                }

                // If no roles are allowed for the field, we should not include it in the schema.
                // Consequently, the field is only added to schema if this conditional evaluates to TRUE.
                if (rolesAllowedForFields.TryGetValue(key: columnName, out IEnumerable<string>? roles))
                {
                    // Roles will not be null here if TryGetValue evaluates to true, so here we check if there are any roles to process.
                    // Since Stored-procedures only support 1 CRUD action, it's possible that stored-procedures might return some values
                    // during mutation operation (i.e, containing one of create/update/delete permission).
                    // Hence, this check is bypassed for stored-procedures.
                    if (roles.Count() > 0 || databaseObject.SourceType is EntitySourceType.StoredProcedure)
                    {
                        if (GraphQLUtils.CreateAuthorizationDirectiveIfNecessary(
                                roles,
                                out DirectiveNode? authZDirective))
                        {
                            directives.Add(authZDirective!);
                        }

                        string exposedColumnName = columnName;
                        if (configEntity.Mappings is not null && configEntity.Mappings.TryGetValue(key: columnName, out string? columnAlias))
                        {
                            exposedColumnName = columnAlias;
                        }

                        NamedTypeNode fieldType = new(GetGraphQLTypeFromSystemType(column.SystemType));
                        FieldDefinitionNode field = new(
                            location: null,
                            new(exposedColumnName),
                            description: null,
                            new List<InputValueDefinitionNode>(),
                            column.IsNullable ? fieldType : new NonNullTypeNode(fieldType),
                            directives);

                        fields.Add(columnName, field);
                    }
                }
            }

            if (configEntity.Relationships is not null)
            {
                foreach ((string relationshipName, EntityRelationship relationship) in configEntity.Relationships)
                {
                    // Generate the field that represents the relationship to ObjectType, so you can navigate through it
                    // and walk the graph
                    string targetEntityName = relationship.TargetEntity.Split('.').Last();
                    Entity referencedEntity = entities[targetEntityName];

                    bool isNullableRelationship = false;

                    if (// Retrieve all the relationship information for the source entity which is backed by this table definition
                        sourceDefinition.SourceEntityRelationshipMap.TryGetValue(entityName, out RelationshipMetadata? relationshipInfo)
                        &&
                        // From the relationship information, obtain the foreign key definition for the given target entity
                        relationshipInfo.TargetEntityToFkDefinitionMap.TryGetValue(targetEntityName,
                            out List<ForeignKeyDefinition>? listOfForeignKeys))
                    {
                        ForeignKeyDefinition? foreignKeyInfo = listOfForeignKeys.FirstOrDefault();

                        // Determine whether the relationship should be nullable by obtaining the nullability
                        // of the referencing(if source entity is the referencing object in the pair)
                        // or referenced columns (if source entity is the referenced object in the pair).
                        if (foreignKeyInfo is not null)
                        {
                            RelationShipPair pair = foreignKeyInfo.Pair;
                            // The given entity may be the referencing or referenced database object in the foreign key
                            // relationship. To determine this, compare with the entity's database object.
                            if (pair.ReferencingDbTable.Equals(databaseObject))
                            {
                                isNullableRelationship = sourceDefinition.IsAnyColumnNullable(foreignKeyInfo.ReferencingColumns);
                            }
                            else
                            {
                                isNullableRelationship = sourceDefinition.IsAnyColumnNullable(foreignKeyInfo.ReferencedColumns);
                            }
                        }
                        else
                        {
                            throw new DataApiBuilderException(
                                message: $"No relationship exists between {entityName} and {targetEntityName}",
                                statusCode: HttpStatusCode.InternalServerError,
                                subStatusCode: DataApiBuilderException.SubStatusCodes.GraphQLMapping);
                        }
                    }

                    INullableTypeNode targetField = relationship.Cardinality switch
                    {
                        Cardinality.One =>
                            new NamedTypeNode(GetDefinedSingularName(targetEntityName, referencedEntity)),
                        Cardinality.Many =>
                            new NamedTypeNode(QueryBuilder.GeneratePaginationTypeName(GetDefinedSingularName(targetEntityName, referencedEntity))),
                        _ =>
                            throw new DataApiBuilderException(
                                message: "Specified cardinality isn't supported",
                                statusCode: HttpStatusCode.InternalServerError,
                                subStatusCode: DataApiBuilderException.SubStatusCodes.GraphQLMapping),
                    };

                    FieldDefinitionNode relationshipField = new(
                        location: null,
                        new NameNode(relationshipName),
                        description: null,
                        new List<InputValueDefinitionNode>(),
                        isNullableRelationship ? targetField : new NonNullTypeNode(targetField),
                        new List<DirectiveNode> {
                            new(RelationshipDirectiveType.DirectiveName,
                                new ArgumentNode("target", GetDefinedSingularName(targetEntityName, referencedEntity)),
                                new ArgumentNode("cardinality", relationship.Cardinality.ToString()))
                        });

                    fields.Add(relationshipField.Name.Value, relationshipField);
                }
            }

            objectTypeDirectives.Add(new(ModelDirectiveType.DirectiveName, new ArgumentNode("name", entityName)));

            if (GraphQLUtils.CreateAuthorizationDirectiveIfNecessary(
                    rolesAllowedForEntity,
                    out DirectiveNode? authorizeDirective))
            {
                objectTypeDirectives.Add(authorizeDirective!);
            }

            // Top-level object type definition name should be singular.
            // The singularPlural.Singular value is used, and if not configured,
            // the top-level entity name value is used. No singularization occurs
            // if the top-level entity name is already plural.
            return new ObjectTypeDefinitionNode(
                location: null,
                name: nameNode,
                description: null,
                objectTypeDirectives,
                new List<NamedTypeNode>(),
                fields.Values.ToImmutableList());
        }

        /// <summary>
        /// Get the GraphQL type equivalent from passed in system Type
        /// </summary>
        /// <param name="type">System type.</param>
        /// <exception cref="DataApiBuilderException">Raised when the provided type does not map to a supported
        /// GraphQL type.</exception>"
        public static string GetGraphQLTypeFromSystemType(Type type)
        {
            return type.Name switch
            {
                "String" => STRING_TYPE,
                "Guid" => UUID_TYPE,
                "Byte" => BYTE_TYPE,
                "Int16" => SHORT_TYPE,
                "Int32" => INT_TYPE,
                "Int64" => LONG_TYPE,
                "Single" => SINGLE_TYPE,
                "Double" => FLOAT_TYPE,
                "Decimal" => DECIMAL_TYPE,
                "Boolean" => BOOLEAN_TYPE,
                "DateTime" => DATETIME_TYPE,
                "DateTimeOffset" => DATETIME_TYPE,
                "Byte[]" => BYTEARRAY_TYPE,
                "TimeOnly" => LOCALTIME_TYPE,
                "TimeSpan" => LOCALTIME_TYPE,
                _ => throw new DataApiBuilderException(
                        message: $"Column type {type} not handled by case. Please add a case resolving {type} to the appropriate GraphQL type",
                        statusCode: HttpStatusCode.InternalServerError,
                        subStatusCode: DataApiBuilderException.SubStatusCodes.GraphQLMapping)
            };
        }

        /// <summary>
        /// Translates system type objects to HotChocolate ObjectValueNode's of the associated value type used for GraphQL schema creation.
        /// The HotChocolate IntValueNode has contructors for integral numeric types (byte, short, long) to
        /// maintain the precision of the input object's value.
        /// </summary>
        /// <param name="metadataValue">Object to be converted to GraphQL ObjectValueNode</param>
        /// <returns>The resulting IValueNode object converted from the input system type object. </returns>
        /// <seealso cref="https://github.com/ChilliCream/graphql-platform/blob/12.18.0/src/HotChocolate/Language/src/Language.SyntaxTree/IntValueNode.cs"/>
        /// <exception cref="DataApiBuilderException">Raised when the input argument's value type does not map to a supported GraphQL type.</exception>
        public static IValueNode CreateValueNodeFromDbObjectMetadata(object metadataValue)
        {
            IValueNode arg = metadataValue switch
            {
                byte value => new ObjectValueNode(new ObjectFieldNode(BYTE_TYPE, new IntValueNode(value))),
                short value => new ObjectValueNode(new ObjectFieldNode(SHORT_TYPE, new IntValueNode(value))),
                int value => new ObjectValueNode(new ObjectFieldNode(INT_TYPE, value)),
                long value => new ObjectValueNode(new ObjectFieldNode(LONG_TYPE, new IntValueNode(value))),
                Guid value => new ObjectValueNode(new ObjectFieldNode(UUID_TYPE, new UuidType().ParseValue(value))),
                string value => new ObjectValueNode(new ObjectFieldNode(STRING_TYPE, value)),
                bool value => new ObjectValueNode(new ObjectFieldNode(BOOLEAN_TYPE, value)),
                float value => new ObjectValueNode(new ObjectFieldNode(SINGLE_TYPE, new SingleType().ParseValue(value))),
                double value => new ObjectValueNode(new ObjectFieldNode(FLOAT_TYPE, value)),
                decimal value => new ObjectValueNode(new ObjectFieldNode(DECIMAL_TYPE, new FloatValueNode(value))),
                DateTimeOffset value => new ObjectValueNode(new ObjectFieldNode(DATETIME_TYPE, new DateTimeType().ParseValue(value))),
                DateTime value => new ObjectValueNode(new ObjectFieldNode(DATETIME_TYPE, new DateTimeType().ParseResult(value))),
                byte[] value => new ObjectValueNode(new ObjectFieldNode(BYTEARRAY_TYPE, new ByteArrayType().ParseValue(value))),
                TimeOnly value => new ObjectValueNode(new ObjectFieldNode(LOCALTIME_TYPE, new LocalTimeType().ParseResult(value))),
                _ => throw new DataApiBuilderException(
                    message: $"The type {metadataValue.GetType()} is not supported as a GraphQL default value",
                    statusCode: HttpStatusCode.InternalServerError,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.GraphQLMapping)
            };

            return arg;
        }
    }
}
