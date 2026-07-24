namespace WechatRobot.Domain.Common;

public abstract class Entity
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity identifiers cannot be empty.", nameof(id));
        }

        Id = id;
    }

    public Guid Id { get; }
}
