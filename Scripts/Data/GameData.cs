using Godot;
using System.Text.Json;
using FileAccess = Godot.FileAccess; // System.IO.FileAccess is also in scope via implicit usings

namespace VerdantCrown.Data;

/// <summary>
/// Boot-time loader for the four <c>Data/*.json</c> files, read via <c>Godot.FileAccess</c>
/// and deserialized with the snake_case policy the JSON uses. Null-safe and load-once:
/// a missing or unparsable file leaves that record at its empty default and logs an error —
/// consumers always null-check <see cref="Player"/> / <see cref="Progression"/>.
/// </summary>
public static class GameData
{
    private const string RoomsPath = "res://Data/rooms.json";
    private const string EnemiesPath = "res://Data/enemies.json";
    private const string PlayerPath = "res://Data/player.json";
    private const string ProgressionPath = "res://Data/progression.json";

    /// <summary>Key of the room array inside rooms.json.</summary>
    private const string RoomsArrayKey = "rooms";

    /// <summary>Key of the enemy array inside enemies.json.</summary>
    private const string EnemiesArrayKey = "enemies";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>True after <see cref="EnsureLoaded"/> has run (even if some files failed).</summary>
    public static bool IsLoaded { get; private set; }

    /// <summary>All rooms from rooms.json; empty when the file failed to load.</summary>
    public static IReadOnlyList<RoomRecord> Rooms { get; private set; } = Array.Empty<RoomRecord>();

    /// <summary>Enemy roster from enemies.json; empty when the file failed to load.</summary>
    public static IReadOnlyList<EnemyRecord> Enemies { get; private set; } = Array.Empty<EnemyRecord>();

    /// <summary>Player stats from player.json; null when the file failed to load.</summary>
    public static PlayerRecord? Player { get; private set; }

    /// <summary>Win/lose conditions and clear flags from progression.json; null on failure.</summary>
    public static ProgressionRecord? Progression { get; private set; }

    /// <summary>Loads the four files exactly once. Safe to call from any consumer's _Ready.</summary>
    public static void EnsureLoaded()
    {
        if (IsLoaded)
        {
            return;
        }

        IsLoaded = true; // set first: a failure must not trigger reload spam every frame
        Rooms = (IReadOnlyList<RoomRecord>?)LoadArray<RoomRecord>(RoomsPath, RoomsArrayKey)
            ?? Array.Empty<RoomRecord>();
        Enemies = (IReadOnlyList<EnemyRecord>?)LoadArray<EnemyRecord>(EnemiesPath, EnemiesArrayKey)
            ?? Array.Empty<EnemyRecord>();
        Player = LoadDocument<PlayerRecord>(PlayerPath);
        Progression = LoadDocument<ProgressionRecord>(ProgressionPath);
    }

    /// <summary>Reads a JSON file's text through FileAccess; null + error when unreadable.</summary>
    private static string? ReadText(string path)
    {
        if (!FileAccess.FileExists(path))
        {
            GD.PrintErr($"GameData: file not found '{path}'.");
            return null;
        }

        using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file is null)
        {
            GD.PrintErr($"GameData: cannot open '{path}' ({FileAccess.GetOpenError()}).");
            return null;
        }

        return file.GetAsText();
    }

    /// <summary>Deserializes a top-level JSON array stored under <paramref name="arrayKey"/>.</summary>
    private static List<T>? LoadArray<T>(string path, string arrayKey) where T : class
    {
        string? text = ReadText(path);
        if (text is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty(arrayKey, out JsonElement array)
                || array.ValueKind != JsonValueKind.Array)
            {
                GD.PrintErr($"GameData: '{path}' has no array '{arrayKey}'.");
                return null;
            }

            var items = new List<T>();
            foreach (JsonElement element in array.EnumerateArray())
            {
                T? item = element.Deserialize<T>(SerializerOptions);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return items;
        }
        catch (JsonException ex)
        {
            GD.PrintErr($"GameData: failed to parse '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Deserializes a whole JSON document object.</summary>
    private static T? LoadDocument<T>(string path) where T : class
    {
        string? text = ReadText(path);
        if (text is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Deserialize<T>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            GD.PrintErr($"GameData: failed to parse '{path}': {ex.Message}");
            return null;
        }
    }
}
