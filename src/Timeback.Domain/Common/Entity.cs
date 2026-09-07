namespace Timeback.Domain.Common;

/// <summary>Base class for entities identified by a surrogate <see cref="Guid"/>.</summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();

    public override bool Equals(object? obj)
        => obj is Entity other && other.GetType() == GetType() && other.Id == Id;

    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>Marker for aggregate roots - the only entities repositories may load or persist directly.</summary>
public abstract class AggregateRoot : Entity;
