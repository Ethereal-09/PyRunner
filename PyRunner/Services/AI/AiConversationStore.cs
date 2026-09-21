using Microsoft.Data.Sqlite;
using PyRunner.Data;
using PyRunner.Models;

namespace PyRunner.Services.AI;

public sealed class AiConversationStore : IAiConversationStore
{
    private const int MaximumMessages = 500;
    private const int MaximumMessageCharacters = 4 * 1024 * 1024;
    private readonly SqliteConnectionFactory _connections;

    public AiConversationStore(SqliteConnectionFactory connections) => _connections = connections;

    public IReadOnlyList<AiConversationSummary> List()
    {
        using var connection = _connections.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Id, c.Title, c.Mode, c.UpdatedAt, COUNT(m.Id)
            FROM AiConversation c
            LEFT JOIN AiMessage m ON m.ConversationId = c.Id
            GROUP BY c.Id, c.Title, c.Mode, c.UpdatedAt
            ORDER BY c.UpdatedAt DESC
            LIMIT 100;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<AiConversationSummary>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), (AiPermissionMode)reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt32(4)));
        return result;
    }

    public AiConversationSnapshot? Load(string id)
    {
        if (!Guid.TryParse(id, out _)) return null;
        using var connection = _connections.CreateOpenConnection();
        string title;
        AiPermissionMode mode;
        DateTimeOffset createdAt, updatedAt;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Title, Mode, CreatedAt, UpdatedAt FROM AiConversation WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            title = reader.GetString(0);
            mode = (AiPermissionMode)reader.GetInt32(1);
            createdAt = ParseTime(reader.GetString(2));
            updatedAt = ParseTime(reader.GetString(3));
        }

        var messages = new List<AiStoredMessage>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, Role, Kind, Content, CreatedAt, RequestId FROM AiMessage
                WHERE ConversationId=$id ORDER BY Sequence LIMIT 500;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                messages.Add(new(reader.GetString(0), (AiMessageRole)reader.GetInt32(1),
                    (AiMessageKind)reader.GetInt32(2), reader.GetString(3), ParseTime(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        var paths = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT FilePath FROM AiContextFile WHERE ConversationId=$id ORDER BY Sequence;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) paths.Add(reader.GetString(0));
        }
        return new(id, title, mode, createdAt, updatedAt, messages, paths);
    }

    public void Save(AiConversationSnapshot conversation)
    {
        if (!Guid.TryParse(conversation.Id, out _)) throw new ArgumentException("Invalid conversation id.");
        var messages = conversation.Messages
            .Where(message => message.Role != AiMessageRole.System)
            .TakeLast(MaximumMessages)
            .ToArray();
        if (messages.Any(message => message.Content.Length > MaximumMessageCharacters))
            throw new ArgumentException("Conversation message is too large.");

        using var connection = _connections.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            INSERT INTO AiConversation(Id, Title, Mode, CreatedAt, UpdatedAt)
            VALUES($id,$title,$mode,$created,$updated)
            ON CONFLICT(Id) DO UPDATE SET Title=$title, Mode=$mode, UpdatedAt=$updated;
            """, ("$id", conversation.Id), ("$title", Limit(conversation.Title, 160)),
            ("$mode", (int)conversation.Mode), ("$created", FormatTime(conversation.CreatedAtUtc)),
            ("$updated", FormatTime(conversation.UpdatedAtUtc)));
        Execute(connection, transaction, "DELETE FROM AiMessage WHERE ConversationId=$id;", ("$id", conversation.Id));
        Execute(connection, transaction, "DELETE FROM AiContextFile WHERE ConversationId=$id;", ("$id", conversation.Id));

        for (var index = 0; index < messages.Length; index++)
        {
            var message = messages[index];
            Execute(connection, transaction, """
                INSERT INTO AiMessage(Id,ConversationId,Sequence,Role,Kind,Content,CreatedAt,RequestId)
                VALUES($messageId,$conversationId,$sequence,$role,$kind,$content,$created,$requestId);
                """, ("$messageId", message.Id), ("$conversationId", conversation.Id), ("$sequence", index),
                ("$role", (int)message.Role), ("$kind", (int)message.Kind), ("$content", message.Content),
                ("$created", FormatTime(message.CreatedAtUtc)), ("$requestId", message.RequestId));
        }

        var paths = conversation.ContextPaths.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        for (var index = 0; index < paths.Length; index++)
            Execute(connection, transaction,
                "INSERT INTO AiContextFile(ConversationId,Sequence,FilePath) VALUES($id,$sequence,$path);",
                ("$id", conversation.Id), ("$sequence", index), ("$path", paths[index]));
        transaction.Commit();
    }

    public void Delete(string id)
    {
        if (!Guid.TryParse(id, out _)) return;
        using var connection = _connections.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AiConversation WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string Limit(string value, int length) =>
        string.IsNullOrWhiteSpace(value) ? "Conversation" : value.Trim()[..Math.Min(value.Trim().Length, length)];
    private static string FormatTime(DateTimeOffset value) => value.UtcDateTime.ToString("O");
    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
}
