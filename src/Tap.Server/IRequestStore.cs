namespace Tap.Server;

public interface IRequestStore
{
    void Add(RequestRecord record);

    IReadOnlyList<RequestRecord> GetAll();

    void Clear();

    IAsyncEnumerable<RequestRecord> Stream(CancellationToken cancellationToken);
}
