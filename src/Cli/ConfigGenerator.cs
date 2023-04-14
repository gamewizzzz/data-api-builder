// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Text.Json;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.Converters;
using Azure.DataApiBuilder.Service;
using Cli.Commands;
using Microsoft.Extensions.Logging;
using static Cli.Utils;

namespace Cli
{
    /// <summary>
    /// Contains the methods for Initializing the config file and Adding/Updating Entities.
    /// </summary>
    public class ConfigGenerator
    {
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        private static ILogger<ConfigGenerator> _logger;
#pragma warning restore CS8618

        public static void SetLoggerForCliConfigGenerator(
            ILogger<ConfigGenerator> configGeneratorLoggerFactory)
        {
            _logger = configGeneratorLoggerFactory;
        }

        /// <summary>
        /// This method will generate the initial config with databaseType and connection-string.
        /// </summary>
        public static bool TryGenerateConfig(InitOptions options, RuntimeConfigLoader loader, IFileSystem fileSystem)
        {
            if (!TryGetConfigFileBasedOnCliPrecedence(loader, options.Config, out string runtimeConfigFile))
            {
                runtimeConfigFile = RuntimeConfigLoader.DefaultName;
                _logger.LogInformation($"Creating a new config file: {runtimeConfigFile}");
            }

            // File existence checked to avoid overwriting the existing configuration.
            if (fileSystem.File.Exists(runtimeConfigFile))
            {
                _logger.LogError("Config file: {runtimeConfigFile} already exists. Please provide a different name or remove the existing config file.", runtimeConfigFile);
                return false;
            }

            // Creating a new json file with runtime configuration
            if (!TryCreateRuntimeConfig(options, loader, out string runtimeConfigJson))
            {
                _logger.LogError($"Failed to create the runtime config file.");
                return false;
            }

            return WriteJsonContentToFile(runtimeConfigFile, runtimeConfigJson, fileSystem);
        }

        /// <summary>
        /// Create a runtime config json string.
        /// </summary>
        /// <param name="options">Init options</param>
        /// <param name="runtimeConfigJson">Output runtime config json.</param>
        /// <returns>True on success. False otherwise.</returns>
        public static bool TryCreateRuntimeConfig(InitOptions options, RuntimeConfigLoader loader, out string runtimeConfigJson)
        {
            runtimeConfigJson = string.Empty;

            DatabaseType dbType = options.DatabaseType;
            string? restPath = options.RestPath;
            Dictionary<string, JsonElement> dbOptions = new();

            switch (dbType)
            {
                case DatabaseType.CosmosDB_NoSQL:
                    string? cosmosDatabase = options.CosmosNoSqlDatabase;
                    string? cosmosContainer = options.CosmosNoSqlContainer;
                    string? graphQLSchemaPath = options.GraphQLSchemaPath;
                    if (string.IsNullOrEmpty(cosmosDatabase) || string.IsNullOrEmpty(graphQLSchemaPath))
                    {
                        _logger.LogError($"Missing mandatory configuration option for CosmosDB_NoSql: --cosmosdb_nosql-database, and --graphql-schema");
                        return false;
                    }

                    if (!File.Exists(graphQLSchemaPath))
                    {
                        _logger.LogError($"GraphQL Schema File: {graphQLSchemaPath} not found.");
                        return false;
                    }

                    // If the option --rest.path is specified for cosmosdb_nosql, log a warning because
                    // rest is not supported for cosmosdb_nosql yet.
                    if (!RestRuntimeOptions.DEFAULT_PATH.Equals(restPath))
                    {
                        _logger.LogWarning("Configuration option --rest.path is not honored for cosmosdb_nosql since it does not support REST yet.");
                    }

                    restPath = null;
                    dbOptions.Add("database", JsonSerializer.SerializeToElement(cosmosDatabase));
                    dbOptions.Add("container", JsonSerializer.SerializeToElement(cosmosContainer));
                    dbOptions.Add("schema", JsonSerializer.SerializeToElement(graphQLSchemaPath));
                    break;

                case DatabaseType.MSSQL:
                    dbOptions.Add("set-session-context", JsonSerializer.SerializeToElement(options.SetSessionContext));
                    break;
                case DatabaseType.MySQL:
                case DatabaseType.PostgreSQL:
                case DatabaseType.CosmosDB_PostgreSQL:
                    break;
                default:
                    throw new Exception($"DatabaseType: ${dbType} not supported.Please provide a valid database-type.");
            }

            DataSource dataSource = new(dbType, string.Empty, dbOptions);

            // default value of connection-string should be used, i.e Empty-string
            // if not explicitly provided by the user
            if (options.ConnectionString is not null)
            {
                dataSource = dataSource with { ConnectionString = options.ConnectionString };
            }

            if (!ValidateAudienceAndIssuerForJwtProvider(options.AuthenticationProvider, options.Audience, options.Issuer))
            {
                return false;
            }

            if (!IsApiPathValid(restPath, "rest") || !IsApiPathValid(options.GraphQLPath, "graphql"))
            {
                return false;
            }

            if (options.RestDisabled && options.GraphQLDisabled)
            {
                _logger.LogError($"Both Rest and GraphQL cannot be disabled together.");
                return false;
            }

            string dabSchemaLink = loader.GetPublishedDraftSchemaLink();

            RuntimeConfig runtimeConfig = new(
                Schema: dabSchemaLink,
                DataSource: dataSource,
                Runtime: new(
                    Rest: new(!options.RestDisabled, restPath ?? RestRuntimeOptions.DEFAULT_PATH),
                    GraphQL: new(!options.GraphQLDisabled, options.GraphQLPath),
                    Host: new(
                        Cors: new(options.CorsOrigin?.ToArray() ?? Array.Empty<string>()),
                        Authentication: new(options.AuthenticationProvider, new(options.Audience, options.Issuer)),
                        Mode: options.HostMode)
                ),
                Entities: new RuntimeEntities(new Dictionary<string, Entity>()));

            runtimeConfigJson = JsonSerializer.Serialize(runtimeConfig, RuntimeConfigLoader.GetSerializationOption());
            return true;
        }

        /// <summary>
        /// This method will add a new Entity with the given REST and GraphQL endpoints, source, and permissions.
        /// It also supports fields that needs to be included or excluded for a given role and operation.
        /// </summary>
        public static bool TryAddEntityToConfigWithOptions(AddOptions options, RuntimeConfigLoader loader, IFileSystem fileSystem)
        {
            if (!TryGetConfigFileBasedOnCliPrecedence(loader, options.Config, out string runtimeConfigFile))
            {
                return false;
            }

            if (!TryReadRuntimeConfig(runtimeConfigFile, out string runtimeConfigJson))
            {
                _logger.LogError($"Failed to read the config file: {runtimeConfigFile}.");
                return false;
            }

            if (!TryAddNewEntity(options, ref runtimeConfigJson))
            {
                _logger.LogError("Failed to add a new entity.");
                return false;
            }

            return WriteJsonContentToFile(runtimeConfigFile, runtimeConfigJson, fileSystem);
        }

        /// <summary>
        /// Add new entity to runtime config json. The function will add new entity to runtimeConfigJson string.
        /// On successful return of the function, runtimeConfigJson will be modified.
        /// </summary>
        /// <param name="options">AddOptions.</param>
        /// <param name="runtimeConfigJson">Json string of existing runtime config. This will be modified on successful return.</param>
        /// <returns>True on success. False otherwise.</returns>
        public static bool TryAddNewEntity(AddOptions options, ref string runtimeConfigJson)
        {
            // Deserialize the json string to RuntimeConfig object.
            //
            RuntimeConfig? runtimeConfig;
            try
            {
                runtimeConfig = JsonSerializer.Deserialize<RuntimeConfig>(runtimeConfigJson, GetSerializationOptions());
                if (runtimeConfig is null)
                {
                    throw new Exception("Failed to parse the runtime config file.");
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed with exception: {e}.");
                return false;
            }

            // If entity exists, we cannot add. Display warning
            //
            if (runtimeConfig.Entities.ContainsKey(options.Entity))
            {
                _logger.LogWarning($"Entity-{options.Entity} is already present. No new changes are added to Config.");
                return false;
            }

            // Try to get the source object as string or DatabaseObjectSource for new Entity
            if (!TryCreateSourceObjectForNewEntity(
                options,
                out EntitySource? source))
            {
                _logger.LogError("Unable to create the source object.");
                return false;
            }

            EntityActionPolicy? policy = GetPolicyForOperation(options.PolicyRequest, options.PolicyDatabase);
            EntityActionFields? field = GetFieldsForOperation(options.FieldsToInclude, options.FieldsToExclude);

            EntityPermission[]? permissionSettings = ParsePermission(options.Permissions, policy, field, options.SourceType);
            if (permissionSettings is null)
            {
                _logger.LogError("Please add permission in the following format. --permissions \"<<role>>:<<actions>>\"");
                return false;
            }

            bool isStoredProcedure = IsStoredProcedure(options);
            // Validations to ensure that REST methods and GraphQL operations can be configured only
            // for stored procedures 
            if (options.GraphQLOperationForStoredProcedure is not null && !isStoredProcedure)
            {
                _logger.LogError("--graphql.operation can be configured only for stored procedures.");
                return false;
            }

            if ((options.RestMethodsForStoredProcedure is not null && options.RestMethodsForStoredProcedure.Any())
                && !isStoredProcedure)
            {
                _logger.LogError("--rest.methods can be configured only for stored procedures.");
                return false;
            }

            GraphQLOperation? graphQLOperationsForStoredProcedures = null;
            SupportedHttpVerb[] SupportedRestMethods = Array.Empty<SupportedHttpVerb>();
            if (isStoredProcedure)
            {
                if (CheckConflictingGraphQLConfigurationForStoredProcedures(options))
                {
                    _logger.LogError("Conflicting GraphQL configurations found.");
                    return false;
                }

                if (!TryAddGraphQLOperationForStoredProcedure(options, out graphQLOperationsForStoredProcedures))
                {
                    return false;
                }

                if (CheckConflictingRestConfigurationForStoredProcedures(options))
                {
                    _logger.LogError("Conflicting Rest configurations found.");
                    return false;
                }

                if (!TryAddSupportedRestMethodsForStoredProcedure(options, out SupportedRestMethods))
                {
                    return false;
                }
            }

            EntityRestOptions restOptions = ConstructRestOptions(options.RestRoute, SupportedRestMethods);
            EntityGraphQLOptions graphqlOptions = ConstructGraphQLTypeDetails(options.GraphQLType, graphQLOperationsForStoredProcedures);

            // Create new entity.
            //
            Entity entity = new(
                Source: source,
                Rest: restOptions,
                GraphQL: graphqlOptions,
                Permissions: permissionSettings,
                Relationships: null,
                Mappings: null);

            // Add entity to existing runtime config.
            IDictionary<string, Entity> entities = runtimeConfig.Entities.Entities;
            entities.Add(options.Entity, entity);
            runtimeConfig = runtimeConfig with { Entities = new(entities) };

            // Serialize runtime config to json string
            //
            runtimeConfigJson = JsonSerializer.Serialize(runtimeConfig, GetSerializationOptions());

            return true;
        }

        /// <summary>
        /// This method creates the source object for a new entity
        /// if the given source fields specified by the user are valid.
        /// </summary>
        public static bool TryCreateSourceObjectForNewEntity(
            AddOptions options,
            [NotNullWhen(true)] out EntitySource? sourceObject)
        {
            sourceObject = null;

            // Try to Parse the SourceType
            if (!Enum.TryParse(options.SourceType, out EntityType objectType))
            {
                _logger.LogError("The source type of {sourceType} is not valid.", options.SourceType);
                return false;
            }

            // Verify that parameter is provided with stored-procedure only
            // and key fields with table/views.
            if (!VerifyCorrectPairingOfParameterAndKeyFieldsWithType(
                    objectType,
                    options.SourceParameters,
                    options.SourceKeyFields))
            {
                return false;
            }

            // Parses the string array to parameter Dictionary
            if (!TryParseSourceParameterDictionary(
                    options.SourceParameters,
                    out Dictionary<string, object>? parametersDictionary))
            {
                return false;
            }

            string[]? sourceKeyFields = null;
            if (options.SourceKeyFields is not null && options.SourceKeyFields.Any())
            {
                sourceKeyFields = options.SourceKeyFields.ToArray();
            }

            // Try to get the source object as string or DatabaseObjectSource
            if (!TryCreateSourceObject(
                    options.Source,
                    objectType,
                    parametersDictionary,
                    sourceKeyFields,
                    out sourceObject))
            {
                _logger.LogError("Unable to parse the given source.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parse permission string to create PermissionSetting array.
        /// </summary>
        /// <param name="permissions">Permission input string as IEnumerable.</param>
        /// <param name="policy">policy to add for this permission.</param>
        /// <param name="fields">fields to include and exclude for this permission.</param>
        /// <param name="sourceType">type of source object.</param>
        /// <returns></returns>
        public static EntityPermission[]? ParsePermission(
            IEnumerable<string> permissions,
            EntityActionPolicy? policy,
            EntityActionFields? fields,
            string? sourceType)
        {
            // Getting Role and Operations from permission string
            string? role, operations;
            if (!TryGetRoleAndOperationFromPermission(permissions, out role, out operations))
            {
                _logger.LogError($"Failed to fetch the role and operation from the given permission string: {string.Join(SEPARATOR, permissions.ToArray())}.");
                return null;
            }

            // Parse the SourceType.
            // Parsing won't fail as this check is already done during source object creation.
            EntityType sourceObjectType = Enum.Parse<EntityType>(sourceType!);
            // Check if provided operations are valid
            if (!VerifyOperations(operations!.Split(","), sourceObjectType))
            {
                return null;
            }

            EntityPermission[] permissionSettings = new[]
            {
                CreatePermissions(role!, operations!, policy, fields)
            };

            return permissionSettings;
        }

        /// <summary>
        /// This method will update an existing Entity with the given REST and GraphQL endpoints, source, and permissions.
        /// It also supports updating fields that need to be included or excluded for a given role and operation.
        /// </summary>
        public static bool TryUpdateEntityWithOptions(UpdateOptions options, RuntimeConfigLoader loader, IFileSystem fileSystem)
        {
            if (!TryGetConfigFileBasedOnCliPrecedence(loader, options.Config, out string runtimeConfigFile))
            {
                return false;
            }

            if (!TryReadRuntimeConfig(runtimeConfigFile, out string runtimeConfigJson))
            {
                _logger.LogError($"Failed to read the config file: {runtimeConfigFile}.");
                return false;
            }

            if (!TryUpdateExistingEntity(options, ref runtimeConfigJson))
            {
                _logger.LogError($"Failed to update the Entity: {options.Entity}.");
                return false;
            }

            return WriteJsonContentToFile(runtimeConfigFile, runtimeConfigJson, fileSystem);
        }

        /// <summary>
        /// Update an existing entity in the runtime config json.
        /// On successful return of the function, runtimeConfigJson will be modified.
        /// </summary>
        /// <param name="options">UpdateOptions.</param>
        /// <param name="runtimeConfigJson">Json string of existing runtime config. This will be modified on successful return.</param>
        /// <returns>True on success. False otherwise.</returns>
        public static bool TryUpdateExistingEntity(UpdateOptions options, ref string runtimeConfigJson)
        {
            // Deserialize the json string to RuntimeConfig object.
            //
            RuntimeConfig? runtimeConfig;
            try
            {
                runtimeConfig = JsonSerializer.Deserialize<RuntimeConfig>(runtimeConfigJson, GetSerializationOptions());
                if (runtimeConfig is null)
                {
                    throw new Exception("Failed to parse the runtime config file.");
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed with exception: {e}.");
                return false;
            }

            // Check if Entity is present
            if (!runtimeConfig.Entities.TryGetValue(options.Entity!, out Entity? entity))
            {
                _logger.LogError($"Entity:{options.Entity} not found. Please add the entity first.");
                return false;
            }

            if (!TryGetUpdatedSourceObjectWithOptions(options, entity, out EntitySource? updatedSource))
            {
                _logger.LogError("Failed to update the source object.");
                return false;
            }

            bool isCurrentEntityStoredProcedure = IsStoredProcedure(entity);
            bool doOptionsRepresentStoredProcedure = options.SourceType is not null && IsStoredProcedure(options);

            // Validations to ensure that REST methods and GraphQL operations can be configured only
            // for stored procedures 
            if (options.GraphQLOperationForStoredProcedure is not null &&
                !(isCurrentEntityStoredProcedure || doOptionsRepresentStoredProcedure))
            {
                _logger.LogError("--graphql.operation can be configured only for stored procedures.");
                return false;
            }

            if ((options.RestMethodsForStoredProcedure is not null && options.RestMethodsForStoredProcedure.Any())
                && !(isCurrentEntityStoredProcedure || doOptionsRepresentStoredProcedure))
            {
                _logger.LogError("--rest.methods can be configured only for stored procedures.");
                return false;
            }

            if (isCurrentEntityStoredProcedure || doOptionsRepresentStoredProcedure)
            {
                if (CheckConflictingGraphQLConfigurationForStoredProcedures(options))
                {
                    _logger.LogError("Conflicting GraphQL configurations found.");
                    return false;
                }

                if (CheckConflictingRestConfigurationForStoredProcedures(options))
                {
                    _logger.LogError("Conflicting Rest configurations found.");
                    return false;
                }
            }

            EntityRestOptions updatedRestDetails = ConstructUpdatedRestDetails(entity, options);
            EntityGraphQLOptions updatedGraphQLDetails = ConstructUpdatedGraphQLDetails(entity, options);
            EntityPermission[]? updatedPermissions = entity!.Permissions;
            Dictionary<string, EntityRelationship>? updatedRelationships = entity.Relationships;
            Dictionary<string, string>? updatedMappings = entity.Mappings;
            EntityActionPolicy updatedPolicy = GetPolicyForOperation(options.PolicyRequest, options.PolicyDatabase);
            EntityActionFields? updatedFields = GetFieldsForOperation(options.FieldsToInclude, options.FieldsToExclude);

            if (false.Equals(updatedGraphQLDetails))
            {
                _logger.LogWarning("Disabling GraphQL for this entity will restrict it's usage in relationships");
            }

            EntityType updatedSourceType = updatedSource.Type;

            if (options.Permissions is not null && options.Permissions.Any())
            {
                // Get the Updated Permission Settings
                updatedPermissions = GetUpdatedPermissionSettings(entity, options.Permissions, updatedPolicy, updatedFields, updatedSourceType);

                if (updatedPermissions is null)
                {
                    _logger.LogError($"Failed to update permissions.");
                    return false;
                }
            }
            else
            {

                if (options.FieldsToInclude is not null && options.FieldsToInclude.Any()
                    || options.FieldsToExclude is not null && options.FieldsToExclude.Any())
                {
                    _logger.LogInformation($"--permissions is mandatory with --fields.include and --fields.exclude.");
                    return false;
                }

                if (options.PolicyRequest is not null || options.PolicyDatabase is not null)
                {
                    _logger.LogInformation($"--permissions is mandatory with --policy-request and --policy-database.");
                    return false;
                }

                if (updatedSourceType is EntityType.StoredProcedure &&
                    !VerifyPermissionOperationsForStoredProcedures(entity.Permissions))
                {
                    return false;
                }
            }

            if (options.Relationship is not null)
            {
                if (!VerifyCanUpdateRelationship(runtimeConfig, options.Cardinality, options.TargetEntity))
                {
                    return false;
                }

                if (updatedRelationships is null)
                {
                    updatedRelationships = new();
                }

                EntityRelationship? new_relationship = CreateNewRelationshipWithUpdateOptions(options);
                if (new_relationship is null)
                {
                    return false;
                }

                updatedRelationships[options.Relationship] = new_relationship;
            }

            if (options.Map is not null && options.Map.Any())
            {
                // Parsing mappings dictionary from Collection
                if (!TryParseMappingDictionary(options.Map, out updatedMappings))
                {
                    return false;
                }
            }

            Entity updatedEntity = new(
                Source: updatedSource,
                Rest: updatedRestDetails,
                GraphQL: updatedGraphQLDetails,
                Permissions: updatedPermissions,
                Relationships: updatedRelationships,
                Mappings: updatedMappings);
            IDictionary<string, Entity> entities = runtimeConfig.Entities.Entities;
            entities[options.Entity] = updatedEntity;
            runtimeConfig = runtimeConfig with { Entities = new(entities) };
            runtimeConfigJson = JsonSerializer.Serialize(runtimeConfig, GetSerializationOptions());
            return true;
        }

        /// <summary>
        /// Get an array of PermissionSetting by merging the existing permissions of an entity with new permissions.
        /// If a role has existing permission and user updates permission of that role,
        /// the old permission will be overwritten. Otherwise, a new permission of the role will be added.
        /// </summary>
        /// <param name="entityToUpdate">entity whose permission needs to be updated</param>
        /// <param name="permissions">New permission to be applied.</param>
        /// <param name="policy">policy to added for this permission</param>
        /// <param name="fields">fields to be included and excluded from the operation permission.</param>
        /// <param name="sourceType">Type of Source object.</param>
        /// <returns> On failure, returns null. Else updated PermissionSettings array will be returned.</returns>
        private static EntityPermission[]? GetUpdatedPermissionSettings(Entity entityToUpdate,
                                                                        IEnumerable<string> permissions,
                                                                        EntityActionPolicy policy,
                                                                        EntityActionFields? fields,
                                                                        EntityType sourceType)
        {
            string? newRole, newOperations;

            // Parse role and operations from the permissions string
            //
            if (!TryGetRoleAndOperationFromPermission(permissions, out newRole, out newOperations))
            {
                _logger.LogError($"Failed to fetch the role and operation from the given permission string: {permissions}.");
                return null;
            }

            List<EntityPermission> updatedPermissionsList = new();
            string[] newOperationArray = newOperations!.Split(",");

            // Verifies that the list of operations declared are valid for the specified sourceType.
            // Example: Stored-procedure can only have 1 operation.
            if (!VerifyOperations(newOperationArray, sourceType))
            {
                return null;
            }

            bool role_found = false;
            // Loop through the current permissions
            foreach (EntityPermission permission in entityToUpdate.Permissions)
            {
                // Find the role that needs to be updated
                if (permission.Role.Equals(newRole))
                {
                    role_found = true;
                    if (sourceType is EntityType.StoredProcedure)
                    {
                        // Since, Stored-Procedures can have only 1 CRUD action. So, when update is requested with new action, we simply replace it.
                        updatedPermissionsList.Add(CreatePermissions(newRole, newOperationArray.First(), policy: null, fields: null));
                    }
                    else if (newOperationArray.Length is 1 && WILDCARD.Equals(newOperationArray[0]))
                    {
                        // If the user inputs WILDCARD as operation, we overwrite the existing operations.
                        updatedPermissionsList.Add(CreatePermissions(newRole!, WILDCARD, policy, fields));
                    }
                    else
                    {
                        // User didn't use WILDCARD, and wants to update some of the operations.
                        IDictionary<EntityActionOperation, EntityAction> existingOperations = ConvertOperationArrayToIEnumerable(permission.Actions, entityToUpdate.Source.Type);

                        // Merge existing operations with new operations
                        EntityAction[] updatedOperationArray = GetUpdatedOperationArray(newOperationArray, policy, fields, existingOperations);

                        updatedPermissionsList.Add(new EntityPermission(newRole, updatedOperationArray));
                    }
                }
                else
                {
                    updatedPermissionsList.Add(permission);
                }
            }

            // If the role we are trying to update is not found, we create a new one
            // and add it to permissionSettings list.
            if (!role_found)
            {
                updatedPermissionsList.Add(CreatePermissions(newRole!, newOperations!, policy, fields));
            }

            return updatedPermissionsList.ToArray();
        }

        /// <summary>
        /// Merge old and new operations into a new list. Take all new updated operations.
        /// Only add existing operations to the merged list if there is no update.
        /// </summary>
        /// <param name="newOperations">operation items to update received from user.</param>
        /// <param name="fieldsToInclude">fields that are included for the operation permission</param>
        /// <param name="fieldsToExclude">fields that are excluded from the operation permission.</param>
        /// <param name="existingOperations">operation items present in the config.</param>
        /// <returns>Array of updated operation objects</returns>
        private static EntityAction[] GetUpdatedOperationArray(string[] newOperations,
                                                        EntityActionPolicy newPolicy,
                                                        EntityActionFields? newFields,
                                                        IDictionary<EntityActionOperation, EntityAction> existingOperations)
        {
            Dictionary<EntityActionOperation, EntityAction> updatedOperations = new();

            EntityActionPolicy existingPolicy = new(null, null);
            EntityActionFields? existingFields = null;

            // Adding the new operations in the updatedOperationList
            foreach (string operation in newOperations)
            {
                // Getting existing Policy and Fields
                if (TryConvertOperationNameToOperation(operation, out EntityActionOperation op))
                {
                    if (existingOperations.ContainsKey(op))
                    {
                        existingPolicy = existingOperations[op].Policy;
                        existingFields = existingOperations[op].Fields;
                    }

                    // Checking if Policy and Field update is required
                    EntityActionPolicy updatedPolicy = newPolicy is null ? existingPolicy : newPolicy;
                    EntityActionFields? updatedFields = newFields is null ? existingFields : newFields;

                    updatedOperations.Add(op, new EntityAction(op, updatedFields, updatedPolicy));
                }
            }

            // Looping through existing operations
            foreach ((EntityActionOperation op, EntityAction act) in existingOperations)
            {
                // If any existing operation doesn't require update, it is added as it is.
                if (!updatedOperations.ContainsKey(op))
                {
                    updatedOperations.Add(op, act);
                }
            }

            return updatedOperations.Values.ToArray();
        }

        /// <summary>
        /// Parses updated options and uses them to create a new sourceObject
        /// for the given entity.
        /// Verifies if the given combination of fields is valid for update
        /// and then it updates it, else it fails.
        /// </summary>
        private static bool TryGetUpdatedSourceObjectWithOptions(
            UpdateOptions options,
            Entity entity,
            [NotNullWhen(true)] out EntitySource? updatedSourceObject)
        {
            updatedSourceObject = null;
            string updatedSourceName = options.Source ?? entity.Source.Object;
            string[]? updatedKeyFields = entity.Source.KeyFields;
            EntityType updatedSourceType = entity.Source.Type;
            Dictionary<string, object>? updatedSourceParameters = entity.Source.Parameters;

            // If SourceType provided by user is null,
            // no update is required.
            if (options.SourceType is not null)
            {
                if (!EnumExtensions.TryDeserialize(options.SourceType, out EntityType? deserializedEntityType))
                {
                    _logger.LogError(EnumExtensions.GenerateMessageForInvalidInput<EntityType>(options.SourceType));
                    return false;
                }

                updatedSourceType = (EntityType)deserializedEntityType;

                if (IsStoredProcedureConvertedToOtherTypes(entity, options) || IsEntityBeingConvertedToStoredProcedure(entity, options))
                {
                    _logger.LogWarning($"Stored procedures can be configured only with {EntityActionOperation.Execute} action whereas tables/views are configured with CRUD actions. Update the actions configured for all the roles for this entity.");
                }

            }

            if (!VerifyCorrectPairingOfParameterAndKeyFieldsWithType(
                    updatedSourceType,
                    options.SourceParameters,
                    options.SourceKeyFields))
            {
                return false;
            }

            // Changing source object from stored-procedure to table/view
            // should automatically update the parameters to be null.
            // Similarly from table/view to stored-procedure, key-fields
            // should be marked null.
            if (EntityType.StoredProcedure.Equals(updatedSourceType))
            {
                updatedKeyFields = null;
            }
            else
            {
                updatedSourceParameters = null;
            }

            // If given SourceParameter is null or is Empty, no update is required.
            // Else updatedSourceParameters will contain the parsed dictionary of parameters.
            if (options.SourceParameters is not null && options.SourceParameters.Any() &&
                !TryParseSourceParameterDictionary(options.SourceParameters, out updatedSourceParameters))
            {
                return false;
            }

            if (options.SourceKeyFields is not null && options.SourceKeyFields.Any())
            {
                updatedKeyFields = options.SourceKeyFields.ToArray();
            }

            // Try Creating Source Object with the updated values.
            if (!TryCreateSourceObject(
                    updatedSourceName,
                    updatedSourceType,
                    updatedSourceParameters,
                    updatedKeyFields,
                    out updatedSourceObject))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// This Method will verify the params required to update relationship info of an entity.
        /// </summary>
        /// <param name="runtimeConfig">runtime config object</param>
        /// <param name="cardinality">cardinality provided by user for update</param>
        /// <param name="targetEntity">name of the target entity for relationship</param>
        /// <returns>Boolean value specifying if given params for update is possible</returns>
        public static bool VerifyCanUpdateRelationship(RuntimeConfig runtimeConfig, string? cardinality, string? targetEntity)
        {
            // CosmosDB doesn't support Relationship
            if (runtimeConfig.DataSource.DatabaseType.Equals(DatabaseType.CosmosDB_NoSQL))
            {
                _logger.LogError("Adding/updating Relationships is currently not supported in CosmosDB.");
                return false;
            }

            // Checking if both cardinality and targetEntity is provided.
            if (cardinality is null || targetEntity is null)
            {
                _logger.LogError("Missing mandatory fields (cardinality and targetEntity) required to configure a relationship.");
                return false;
            }

            // Add/Update of relationship is not allowed when GraphQL is disabled in Global Runtime Settings
            if (!runtimeConfig.Runtime.GraphQL.Enabled)
            {
                _logger.LogError("Cannot add/update relationship as GraphQL is disabled in the global runtime settings of the config.");
                return false;
            }

            // Both the source entity and target entity needs to present in config to establish relationship.
            if (!runtimeConfig.Entities.ContainsKey(targetEntity))
            {
                _logger.LogError($"Entity:{targetEntity} is not present. Relationship cannot be added.");
                return false;
            }

            // Check if provided value of cardinality is present in the enum.
            if (!string.Equals(cardinality, Cardinality.One.ToString(), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cardinality, Cardinality.Many.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError($"Failed to parse the given cardinality : {cardinality}. Supported values are one/many.");
                return false;
            }

            // If GraphQL is disabled, entity cannot be used in relationship
            if (false.Equals(runtimeConfig.Entities[targetEntity].GraphQL))
            {
                _logger.LogError($"Entity: {targetEntity} cannot be used in relationship as it is disabled for GraphQL.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// This Method will create a new Relationship Object based on the given UpdateOptions.
        /// </summary>
        /// <param name="options">update options </param>
        /// <returns>Returns a Relationship Object</returns>
        public static EntityRelationship? CreateNewRelationshipWithUpdateOptions(UpdateOptions options)
        {
            string[]? updatedSourceFields = null;
            string[]? updatedTargetFields = null;
            string[]? updatedLinkingSourceFields = options.LinkingSourceFields is null || !options.LinkingSourceFields.Any() ? null : options.LinkingSourceFields.ToArray();
            string[]? updatedLinkingTargetFields = options.LinkingTargetFields is null || !options.LinkingTargetFields.Any() ? null : options.LinkingTargetFields.ToArray();

            Cardinality updatedCardinality = Enum.Parse<Cardinality>(options.Cardinality!, ignoreCase: true);

            if (options.RelationshipFields is not null && options.RelationshipFields.Any())
            {
                // Getting source and target fields from mapping fields
                //
                if (options.RelationshipFields.Count() != 2)
                {
                    _logger.LogError("Please provide the --relationship.fields in the correct format using ':' between source and target fields.");
                    return null;
                }

                updatedSourceFields = options.RelationshipFields.ElementAt(0).Split(",");
                updatedTargetFields = options.RelationshipFields.ElementAt(1).Split(",");
            }

            return new EntityRelationship(
                Cardinality: updatedCardinality,
                TargetEntity: options.TargetEntity!,
                SourceFields: updatedSourceFields ?? Array.Empty<string>(),
                TargetFields: updatedTargetFields ?? Array.Empty<string>(),
                LinkingObject: options.LinkingObject,
                LinkingSourceFields: updatedLinkingSourceFields ?? Array.Empty<string>(),
                LinkingTargetFields: updatedLinkingTargetFields ?? Array.Empty<string>());
        }

        /// <summary>
        /// This method will try starting the engine.
        /// It will use the config provided by the user, else will look for the default config.
        /// Does validation to check connection string is not null or empty.
        /// </summary>
        public static bool TryStartEngineWithOptions(StartOptions options, RuntimeConfigLoader loader)
        {
            if (!TryGetConfigFileBasedOnCliPrecedence(loader, options.Config, out string runtimeConfigFile))
            {
                _logger.LogError("Config not provided and default config file doesn't exist.");
                return false;
            }

            // Validates that config file has data and follows the correct json schema
            if (!CanParseConfigCorrectly(runtimeConfigFile, out RuntimeConfig? deserializedRuntimeConfig))
            {
                return false;
            }

            /// This will add arguments to start the runtime engine with the config file.
            List<string> args = new()
            { "--" + nameof(RuntimeConfigLoader.CONFIGFILE_NAME), runtimeConfigFile };

            /// Add arguments for LogLevel. Checks if LogLevel is overridden with option `--LogLevel`.
            /// If not provided, Default minimum LogLevel is Debug for Development mode and Error for Production mode.
            LogLevel minimumLogLevel;
            if (options.LogLevel is not null)
            {
                if (options.LogLevel is < LogLevel.Trace or > LogLevel.None)
                {
                    _logger.LogError($"LogLevel's valid range is 0 to 6, your value: {options.LogLevel}, see: " +
                        $"https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.loglevel?view=dotnet-plat-ext-7.0");
                    return false;
                }

                minimumLogLevel = (LogLevel)options.LogLevel;
                _logger.LogInformation($"Setting minimum LogLevel: {minimumLogLevel}.");
            }
            else
            {
                minimumLogLevel = Startup.GetLogLevelBasedOnMode(deserializedRuntimeConfig);
                HostMode hostModeType = deserializedRuntimeConfig.Runtime.Host.Mode;

                _logger.LogInformation($"Setting default minimum LogLevel: {minimumLogLevel} for {hostModeType} mode.");
            }

            args.Add("--LogLevel");
            args.Add(minimumLogLevel.ToString());

            // This will add args to disable automatic redirects to https if specified by user
            if (options.IsHttpsRedirectionDisabled)
            {
                args.Add(Startup.NO_HTTPS_REDIRECT_FLAG);
            }

            return Azure.DataApiBuilder.Service.Program.StartEngine(args.ToArray());
        }

        /// <summary>
        /// Returns an array of SupportedRestMethods resolved from command line input (EntityOptions).
        /// When no methods are specified, the default "POST" is returned.
        /// </summary>
        /// <param name="options">Entity configuration options received from command line input.</param>
        /// <param name="SupportedRestMethods">Rest methods to enable for stored procedure.</param>
        /// <returns>True when the default (POST) or user provided stored procedure REST methods are supplied.
        /// Returns false and an empty array when an invalid REST method is provided.</returns>
        private static bool TryAddSupportedRestMethodsForStoredProcedure(EntityOptions options, [NotNullWhen(true)] out SupportedHttpVerb[] SupportedRestMethods)
        {
            if (options.RestMethodsForStoredProcedure is null || !options.RestMethodsForStoredProcedure.Any())
            {
                SupportedRestMethods = new[] { SupportedHttpVerb.Post };
            }
            else
            {
                SupportedRestMethods = CreateRestMethods(options.RestMethodsForStoredProcedure);
            }

            return SupportedRestMethods.Length > 0;
        }

        /// <summary>
        /// Identifies the graphQL operations configured for the stored procedure from add command.
        /// When no value is specified, the stored procedure is configured with a mutation operation.
        /// Returns true/false corresponding to a successful/unsuccessful conversion of the operations.
        /// </summary>
        /// <param name="options">GraphQL operations configured for the Stored Procedure using add command</param>
        /// <param name="graphQLOperationForStoredProcedure">GraphQL Operations as Enum type</param>
        /// <returns>True when a user declared GraphQL operation on a stored procedure backed entity is supported. False, otherwise.</returns>
        private static bool TryAddGraphQLOperationForStoredProcedure(EntityOptions options, [NotNullWhen(true)] out GraphQLOperation? graphQLOperationForStoredProcedure)
        {
            if (options.GraphQLOperationForStoredProcedure is null)
            {
                graphQLOperationForStoredProcedure = GraphQLOperation.Mutation;
            }
            else
            {
                if (!TryConvertGraphQLOperationNameToGraphQLOperation(options.GraphQLOperationForStoredProcedure, out GraphQLOperation operation))
                {
                    graphQLOperationForStoredProcedure = null;
                    return false;
                }

                graphQLOperationForStoredProcedure = operation;
            }

            return true;
        }

        /// <summary>
        /// Constructs the updated REST settings based on the input from update command and
        /// existing REST configuration for an entity
        /// </summary>
        /// <param name="entity">Entity for which the REST settings are updated</param>
        /// <param name="options">Input from update command</param>
        /// <returns>Boolean -> when the entity's REST configuration is true/false.
        /// RestEntitySettings -> when a non stored procedure entity is configured with granular REST settings (Path).
        /// RestStoredProcedureEntitySettings -> when a stored procedure entity is configured with explicit SupportedRestMethods.
        /// RestStoredProcedureEntityVerboseSettings-> when a stored procedure entity is configured with explicit SupportedRestMethods and Path settings.</returns>
        private static EntityRestOptions ConstructUpdatedRestDetails(Entity entity, EntityOptions options)
        {
            // Updated REST Route details
            EntityRestOptions restPath = (options.RestRoute is not null) ? ConstructRestOptions(options.RestRoute, Array.Empty<SupportedHttpVerb>()) : entity.Rest;

            // Updated REST Methods info for stored procedures
            SupportedHttpVerb[]? SupportedRestMethods;
            if (!IsStoredProcedureConvertedToOtherTypes(entity, options)
                && (IsStoredProcedure(entity) || IsStoredProcedure(options)))
            {
                if (options.RestMethodsForStoredProcedure is null || !options.RestMethodsForStoredProcedure.Any())
                {
                    SupportedRestMethods = entity.Rest.Methods;
                }
                else
                {
                    SupportedRestMethods = CreateRestMethods(options.RestMethodsForStoredProcedure);
                }
            }
            else
            {
                SupportedRestMethods = null;
            }

            if (!restPath.Enabled)
            {
                // Non-stored procedure scenario when the REST endpoint is disabled for the entity.
                if (options.RestRoute is not null)
                {
                    SupportedRestMethods = null;
                }
                else
                {
                    if (options.RestMethodsForStoredProcedure is not null && options.RestMethodsForStoredProcedure.Any())
                    {
                        restPath = restPath with { Enabled = false };
                    }
                }
            }

            if (IsEntityBeingConvertedToStoredProcedure(entity, options)
               && (SupportedRestMethods is null || SupportedRestMethods.Length == 0))
            {
                SupportedRestMethods = new SupportedHttpVerb[] { SupportedHttpVerb.Post };
            }

            return restPath with { Methods = SupportedRestMethods ?? Array.Empty<SupportedHttpVerb>() };
        }

        /// <summary>
        /// Constructs the updated GraphQL settings based on the input from update command and
        /// existing graphQL configuration for an entity
        /// </summary>
        /// <param name="entity">Entity for which GraphQL settings are updated</param>
        /// <param name="options">Input from update command</param>
        /// <returns>Boolean -> when the entity's GraphQL configuration is true/false.
        /// GraphQLEntitySettings -> when a non stored procedure entity is configured with granular GraphQL settings (Type/Singular/Plural).
        /// GraphQLStoredProcedureEntitySettings -> when a stored procedure entity is configured with an explicit operation.
        /// GraphQLStoredProcedureEntityVerboseSettings-> when a stored procedure entity is configured with explicit operation and type settings.</returns>
        private static EntityGraphQLOptions ConstructUpdatedGraphQLDetails(Entity entity, EntityOptions options)
        {
            //Updated GraphQL Type
            EntityGraphQLOptions graphQLType = (options.GraphQLType is not null) ? ConstructGraphQLTypeDetails(options.GraphQLType, null) : entity.GraphQL;
            GraphQLOperation? graphQLOperation = null;

            if (!IsStoredProcedureConvertedToOtherTypes(entity, options)
                && (IsStoredProcedure(entity) || IsStoredProcedure(options)))
            {
                if (options.GraphQLOperationForStoredProcedure is not null)
                {
                    GraphQLOperation operation;
                    if (TryConvertGraphQLOperationNameToGraphQLOperation(options.GraphQLOperationForStoredProcedure, out operation))
                    {
                        graphQLOperation = operation;
                    }
                    else
                    {
                        graphQLOperation = null;
                    }
                }
            }
            else
            {
                graphQLOperation = null;
            }

            if (!graphQLType.Enabled)
            {
                if (options.GraphQLType is not null)
                {
                    graphQLOperation = null;
                }
                else
                {
                    if (options.GraphQLOperationForStoredProcedure is null)
                    {
                        graphQLOperation = null;
                    }
                    else
                    {
                        graphQLType = graphQLType with { Enabled = false };
                    }
                }
            }

            if (IsEntityBeingConvertedToStoredProcedure(entity, options) && graphQLOperation is null)
            {
                graphQLOperation = GraphQLOperation.Mutation;
            }

            return graphQLType with { Operation = graphQLOperation };
        }
    }
}
