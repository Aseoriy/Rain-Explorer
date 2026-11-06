using RainArchives.Contracts;

namespace RainArchives.Client;

public sealed class ArchiveClientException : Exception
{
    public ArchiveClientException(ArchiveError error)
        : base(error.Message)
    {
        Error = error;
    }

    public ArchiveError Error { get; }
}
