using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.DataApiBuilder.Config.Converters;

namespace Azure.DataApiBuilder.Config;

public enum EntitySourceType
{
    Table,
    View,
    [EnumMember(Value = "stored-procedure")] StoredProcedure
}

/// <summary>
/// The operations supported by the service.
/// </summary>
public enum EntityActionOperation
{
    None,

    // *
    [EnumMember(Value = "*")] All,

    // Common Operations
    Delete, Read,

    // cosmosdb_nosql operations
    Upsert, Create,

    // Sql operations
    Insert, Update, UpdateGraphQL,

    // Additional
    UpsertIncremental, UpdateIncremental,

    // Only valid operation for stored procedures
    Execute
}

/// <summary>
/// A subset of the HTTP verb list that is supported by the REST endpoints within the service.
/// </summary>
public enum SupportedHttpVerb
{
    Get,
    Post,
    Put,
    Patch,
    Delete
}

public enum GraphQLOperation
{
    Query,
    Mutation
}

public enum Cardinality
{
    One,
    Many
}

public record EntitySource(string Object, EntitySourceType Type, Dictionary<string, object>? Parameters, string[]? KeyFields);

[JsonConverter(typeof(EntityGraphQLOptionsConverter))]
public record EntityGraphQLOptions(string Singular, string Plural, bool Enabled = true, GraphQLOperation? Operation = null);

[JsonConverter(typeof(EntityRestOptionsConverter))]
public record EntityRestOptions(SupportedHttpVerb[] Methods, string? Path = null, bool Enabled = true)
{
    public static readonly SupportedHttpVerb[] DEFAULT_SUPPORTED_VERBS = new[] { SupportedHttpVerb.Get, SupportedHttpVerb.Post, SupportedHttpVerb.Put, SupportedHttpVerb.Patch, SupportedHttpVerb.Delete };
}

public record EntityActionFields(HashSet<string> Exclude, HashSet<string>? Include = null);

public record EntityActionPolicy(string? Request = null, string? Database = null)
{
    public string ProcessedDatabaseFields()
    {
        if (Database is null)
        {
            throw new NullReferenceException("Unable to process the fields in the database policy because the policy is null.");
        }

        return ProcessFieldsInPolicy(Database);
    }

    /// <summary>
    /// Helper method which takes in the database policy and returns the processed policy
    /// without @item. directives before field names.
    /// </summary>
    /// <param name="policy">Raw database policy</param>
    /// <returns>Processed policy without @item. directives before field names.</returns>
    private static string ProcessFieldsInPolicy(string? policy)
    {
        if (policy is null)
        {
            return string.Empty;
        }

        string fieldCharsRgx = @"@item\.([a-zA-Z0-9_]*)";

        // processedPolicy would be devoid of @item. directives.
        string processedPolicy = Regex.Replace(policy, fieldCharsRgx, (columnNameMatch) =>
            columnNameMatch.Groups[1].Value
        );
        return processedPolicy;
    }
}

public record EntityAction(EntityActionOperation Action, EntityActionFields? Fields, EntityActionPolicy Policy)
{
    public static readonly HashSet<EntityActionOperation> ValidPermissionOperations = new() { EntityActionOperation.Create, EntityActionOperation.Read, EntityActionOperation.Update, EntityActionOperation.Delete };
    public static readonly HashSet<EntityActionOperation> ValidStoredProcedurePermissionOperations = new() { EntityActionOperation.Execute };
}

public record EntityPermission(string Role, EntityAction[] Actions);

public record EntityRelationship(
    Cardinality Cardinality,
    [property: JsonPropertyName("target.entity")] string TargetEntity,
    [property: JsonPropertyName("source.fields")] string[] SourceFields,
    [property: JsonPropertyName("target.fields")] string[] TargetFields,
    [property: JsonPropertyName("linking.object")] string? LinkingObject,
    [property: JsonPropertyName("linking.source.fields")] string[] LinkingSourceFields,
    [property: JsonPropertyName("linking.target.fields")] string[] LinkingTargetFields);

public record Entity(
    EntitySource Source,
    EntityGraphQLOptions GraphQL,
    EntityRestOptions Rest,
    EntityPermission[] Permissions,
    Dictionary<string, string>? Mappings,
    Dictionary<string, EntityRelationship>? Relationships);
