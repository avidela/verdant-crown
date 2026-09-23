using Godot;
using VerdantCrown.Combat;
using VerdantCrown.Core;
using VerdantCrown.Data;

namespace VerdantCrown.World;

/// <summary>
/// Player hit points as a child node of Player.tscn: seeded from <c>player.json</c>
/// (<c>hp_max</c> via <see cref="GameData"/>), publishes damage through
/// <see cref="EventBus.RaisePlayerHudChanged"/> so the HUD re-reads real values, and on
/// death moves <see cref="GameManager"/> into the GameOver state.
/// <para>Damage sources (enemy contact via <see cref="EnemyBrain"/>, hazards) call
/// <see cref="TakeDamage"/>; every successful hit opens
/// <see cref="InvulnWindowSeconds"/> of i-frames so touching enemies debounce instead
/// of shredding the player. Heart pickups call <see cref="Heal"/>.</para>
/// </summary>
public partial class PlayerHealth : Node
{
    /// <summary>Mirrors player.json hp_max — used only when the file fails to load.</summary>
    private const int FallbackHpMax = 6;

    /// <summary>
    /// Seconds of i-frames after a successful hit. Contact damage is re-checked every
    /// physics tick by enemies, so this window is the debounce that makes one touch = one hit.
    /// </summary>
    public const float InvulnWindowSeconds = 0.8f;

    /// <summary>Current hit points; never reported below zero.</summary>
    public int Hp { get; private set; }

    /// <summary>Maximum hit points from player.json hp_max.</summary>
    public int HpMax { get; private set; } = FallbackHpMax;

    /// <summary>True once HP has reached zero.</summary>
    public bool IsDead => Hp <= 0;

    /// <summary>True while the i-frame window (post-hit or granted) is still open.</summary>
    public bool IsInvulnerable => _invulnRemaining > 0f;

    /// <summary>Seconds of i-frames left on the current window (0 when vulnerable).</summary>
    public float InvulnerabilityRemaining => _invulnRemaining;

    private float _invulnRemaining;

    public override void _Ready()
    {
        GameData.EnsureLoaded();

        if (GameData.Player is null)
        {
            GD.PushError($"PlayerHealth: player.json unavailable — using fallback max HP {FallbackHpMax}.");
        }
        else
        {
            HpMax = GameData.Player.HpMax;
            GD.Print($"Player '{GameData.Player.Id}' ({GameData.Player.Name}) ready — {HpMax} HP.");
        }

        Hp = HpMax;
        EventBus.RaisePlayerHudChanged(); // HUD may already be subscribed after scene swaps
    }

    public override void _Process(double delta)
    {
        if (_invulnRemaining > 0f)
        {
            _invulnRemaining = Mathf.Max(0f, _invulnRemaining - (float)delta);
        }
    }

    /// <summary>Reduces HP inside the i-frame window, refreshes the HUD, and enters GameOver at zero. Ignores non-positive amounts, hits during <see cref="InvulnWindowSeconds"/>, and post-death hits.</summary>
    public void TakeDamage(int amount, string? source = null)
    {
        if (amount <= 0 || IsDead || IsInvulnerable)
        {
            return;
        }

        _invulnRemaining = InvulnWindowSeconds;
        Hp = Mathf.Max(0, Hp - amount);
        GD.Print(source is null
            ? $"Player took {amount} damage ({Hp}/{HpMax} HP)."
            : $"Player took {amount} damage from {source} ({Hp}/{HpMax} HP).");
        EventBus.RaisePlayerHudChanged();

        if (Hp <= 0)
        {
            Die();
        }
    }

    /// <summary>
    /// Grants <paramref name="seconds"/> of i-frames from right now, reusing the same
    /// timer that backs the post-hit <see cref="InvulnWindowSeconds"/> window (so
    /// <see cref="IsInvulnerable"/> gates <see cref="TakeDamage"/> as usual). The timer
    /// is set to the GREATER of the remaining time and <paramref name="seconds"/>, so a
    /// shorter grant can never shorten an existing window. The dodge uses this for its
    /// i-frames; ignores non-positive amounts and the dead.
    /// </summary>
    public void GrantInvulnerability(float seconds)
    {
        if (seconds <= 0f || IsDead)
        {
            return;
        }

        _invulnRemaining = Mathf.Max(_invulnRemaining, seconds);
    }

    /// <summary>
    /// Restores up to <paramref name="amount"/> HP, clamped at <see cref="HpMax"/>, and
    /// refreshes the HUD. Heart pickups call this; ignores non-positive amounts, overheal,
    /// and the dead.
    /// </summary>
    public void Heal(int amount)
    {
        if (amount <= 0 || IsDead || Hp >= HpMax)
        {
            return;
        }

        Hp = Mathf.Min(HpMax, Hp + amount);
        GD.Print($"Player healed {amount} ({Hp}/{HpMax} HP).");
        EventBus.RaisePlayerHudChanged();
    }

    /// <summary>
    /// Full revive for end-of-run recovery (Sprint 2.8): refills to <see cref="HpMax"/>,
    /// clears the i-frame window, and refreshes the HUD. Unlike <see cref="Heal"/> this
    /// deliberately works while dead (<see cref="IsDead"/>) so the GameOver retry can bring
    /// the pilot back; the Victory continue uses it too. Taking damage mid-recovery is
    /// gated as usual by whatever i-frames a fresh room load leaves open (none — the
    /// caller reloads the room, which repositions the player at PlayerStart).
    /// </summary>
    public void RestoreFull()
    {
        bool wasDead = IsDead;
        Hp = HpMax;
        _invulnRemaining = 0f;
        GD.Print(wasDead
            ? $"Player revived at full health ({Hp}/{HpMax} HP)."
            : $"Player restored to full health ({Hp}/{HpMax} HP).");
        EventBus.RaisePlayerHudChanged();
    }

    private void Die()
    {
        // Lose state name comes from progression.json lose_condition.result_state.
        string? resultState = GameData.Progression?.LoseCondition?.ResultState;
        GameManager.GameState state = GameManager.GameState.GameOver;
        if (!string.IsNullOrEmpty(resultState)
            && !Enum.TryParse(resultState, out state))
        {
            GD.PrintErr($"PlayerHealth: unknown lose result_state '{resultState}' — using GameOver.");
            state = GameManager.GameState.GameOver;
        }

        GD.Print("Player defeated — GAME OVER.");
        GameManager? gameManager = GameManager.Instance;
        if (gameManager is not null)
        {
            gameManager.SetState(state);
        }
    }
}
