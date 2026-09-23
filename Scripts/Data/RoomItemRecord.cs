namespace VerdantCrown.Data;

/// <summary>One item declared for a room in rooms.json (only the fields the game layer reads).</summary>
public sealed record RoomItemRecord
{
    /// <summary>Entry id — for the win item this doubles as the scene node name in PascalCase (crown_fragment → CrownFragment).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Real item identity when the entry declares one (items[].item_id, e.g. "key" →
    /// "temple_key"); null when the entry omits it. See <see cref="EffectiveItemId"/>.
    /// </summary>
    public string? ItemId { get; init; }

    /// <summary>Spawn/collection condition (e.g. "boss_defeated"); null = always available.</summary>
    public string? Condition { get; init; }

    /// <summary>
    /// The item identity consumers should use: <c>item_id</c> when present
    /// ({"id":"key","item_id":"temple_key"} → "temple_key"), falling back to <see cref="Id"/>
    /// when the entry only declares <c>id</c>. Fixes the temple_key schema split on the
    /// record side so rooms.json entries keep working either way.
    /// </summary>
    public string EffectiveItemId => string.IsNullOrEmpty(ItemId) ? Id : ItemId;
}
