namespace VerdantCrown.Combat;

/// <summary>
/// Anything in the world that can receive damage — enemies, breakable props, dummies.
/// Implementers are typically <c>Node3D</c>s attached to (or being) a physics body so
/// hitboxes can discover them from a detected collision object.
/// </summary>
public interface IDamageable
{
    /// <summary>
    /// Applies <paramref name="amount"/> points of damage.
    /// Implementations ignore non-positive amounts and hits received after defeat.
    /// </summary>
    void TakeDamage(int amount);
}
