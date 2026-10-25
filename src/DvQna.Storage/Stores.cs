using System.Text.Json;
using DvQna.Core;
using Microsoft.Data.Sqlite;

namespace DvQna.Storage;

/// <summary>Connection profiles in profiles.json (no secrets).</summary>
public sealed class ProfileStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public ProfileStore(AppSettings settings)
    {
        Directory.CreateDirectory(settings.DataDirectory);
        _path = Path.Combine(settings.DataDirectory, "profiles.json");
    }

    public List<EnvironmentProfile> All()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<EnvironmentProfile>>(File.ReadAllText(_path), Json.Options) ?? [];
        }
    }

    public EnvironmentProfile? Find(string nameOrUrl)
    {
        var all = All();
        return all.FirstOrDefault(p => p.Name.Equals(nameOrUrl, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(p => p.Url.TrimEnd('/').Equals(nameOrUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(p => p.Name.Contains(nameOrUrl, StringComparison.OrdinalIgnoreCase));
    }

    public void Upsert(EnvironmentProfile profile)
    {
        lock (_lock)
        {
            var all = All().Where(p => !p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            all.Add(profile);
            File.WriteAllText(_path, JsonSerializer.Serialize(all.OrderBy(p => p.Name), Json.Pretty));
        }
    }

    public bool Remove(string name)
    {
        lock (_lock)
        {
            var all = All();
            var removed = all.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            File.WriteAllText(_path, JsonSerializer.Serialize(all, Json.Pretty));
            return removed;
        }
    }
}

public sealed record ConversationSummary(string Id, string Title, DateTimeOffset UpdatedAt);
public sealed record StoredMessage(string Role, string Content, DateTimeOffset At);
public sealed record DatasetInfo(string Id, string Tool, string Env, int RowCount, DateTimeOffset FetchedAt);

/// <summary>SQLite store for conversations, the tool-call audit log and datasets.</summary>
public sealed class AppStore
{
    private readonly string _cs;

    public AppStore(AppSettings settings)
    {
        Directory.CreateDirectory(settings.DataDirectory);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(settings.DataDirectory, "app.db") }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS conversations (id TEXT PRIMARY KEY, title TEXT, updated_at TEXT);
            CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY AUTOINCREMENT, conversation_id TEXT, role TEXT, content TEXT, at TEXT);
            CREATE TABLE IF NOT EXISTS tool_calls (id INTEGER PRIMARY KEY AUTOINCREMENT, conversation_id TEXT, tool TEXT, input TEXT,
                summary TEXT, row_count INTEGER, dataset_id TEXT, duration_ms INTEGER, error TEXT, at TEXT);
            CREATE TABLE IF NOT EXISTS datasets (id TEXT PRIMARY KEY, conversation_id TEXT, tool TEXT, env TEXT, row_count INTEGER,
                rows TEXT, sources TEXT, fetched_at TEXT);
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void TouchConversation(string id, string title)
    {
        using var c = Open();
        Exec(c, "INSERT INTO conversations(id,title,updated_at) VALUES($id,$t,$u) ON CONFLICT(id) DO UPDATE SET updated_at=$u",
            ("$id", id), ("$t", title), ("$u", DateTimeOffset.UtcNow.ToString("O")));
    }

    public void AddMessage(string conversationId, string role, string content)
    {
        using var c = Open();
        Exec(c, "INSERT INTO messages(conversation_id,role,content,at) VALUES($c,$r,$m,$a)",
            ("$c", conversationId), ("$r", role), ("$m", content), ("$a", DateTimeOffset.UtcNow.ToString("O")));
    }

    public List<ConversationSummary> Conversations()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,title,updated_at FROM conversations ORDER BY updated_at DESC LIMIT 100";
        using var r = cmd.ExecuteReader();
        var list = new List<ConversationSummary>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2))));
        return list;
    }

    public List<StoredMessage> Messages(string conversationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT role,content,at FROM messages WHERE conversation_id=$c ORDER BY id";
        cmd.Parameters.AddWithValue("$c", conversationId);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredMessage>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2))));
        return list;
    }

    public void AuditToolCall(string conversationId, string tool, string input, ToolResult? result, long durationMs, string? error)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO tool_calls(conversation_id,tool,input,summary,row_count,dataset_id,duration_ms,error,at)
            VALUES($c,$t,$i,$s,$n,$d,$ms,$e,$a)
            """,
            ("$c", conversationId), ("$t", tool), ("$i", input), ("$s", result?.Summary), ("$n", result?.RowCount),
            ("$d", result?.DatasetId), ("$ms", durationMs), ("$e", error), ("$a", DateTimeOffset.UtcNow.ToString("O")));
    }

    public string SaveDataset(string conversationId, string tool, string env, List<Dictionary<string, object?>> rows, List<ToolSource> sources)
    {
        var id = "ds_" + Guid.NewGuid().ToString("N")[..8];
        using var c = Open();
        Exec(c, "INSERT INTO datasets(id,conversation_id,tool,env,row_count,rows,sources,fetched_at) VALUES($id,$c,$t,$e,$n,$r,$s,$f)",
            ("$id", id), ("$c", conversationId), ("$t", tool), ("$e", env), ("$n", rows.Count),
            ("$r", JsonSerializer.Serialize(rows, Json.Options)), ("$s", JsonSerializer.Serialize(sources, Json.Options)),
            ("$f", DateTimeOffset.UtcNow.ToString("O")));
        return id;
    }

    public (DatasetInfo Info, JsonElement Rows, JsonElement Sources)? GetDataset(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,tool,env,row_count,fetched_at,rows,sources FROM datasets WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var info = new DatasetInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), DateTimeOffset.Parse(r.GetString(4)));
        return (info, JsonDocument.Parse(r.GetString(5)).RootElement.Clone(), JsonDocument.Parse(r.GetString(6)).RootElement.Clone());
    }
}
