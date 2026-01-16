namespace Cinema.Domain.Common;

public record EntityId<T>(Guid Value)
{
    public static EntityId<T> Empty() => new(Guid.Empty);
    public static EntityId<T> New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
