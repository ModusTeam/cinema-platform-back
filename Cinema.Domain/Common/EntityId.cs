using System;
using System.Text.Json.Serialization;

namespace Cinema.Domain.Common;

public readonly record struct EntityId<T>(Guid Value) : IComparable<EntityId<T>>
{
    [JsonConstructor]
    public EntityId(string value) : this(Guid.Parse(value)) {}

    public static EntityId<T> Empty => new(Guid.Empty);
    public static EntityId<T> New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
    
    public int CompareTo(EntityId<T> other) => Value.CompareTo(other.Value);
    
    public static implicit operator Guid(EntityId<T> id) => id.Value;
    public static explicit operator EntityId<T>(Guid id) => new(id);
}