namespace RainArchives.Contracts;

public readonly record struct ArchiveSessionId(Guid Value)
{
    public static ArchiveSessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct EntryId(Guid Value)
{
    public static EntryId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct OperationId(Guid Value)
{
    public static OperationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct EntryStreamId(Guid Value)
{
    public static EntryStreamId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}
