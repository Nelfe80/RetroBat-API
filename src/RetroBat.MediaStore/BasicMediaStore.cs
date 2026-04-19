using RetroBat.Domain.Interfaces;

namespace RetroBat.MediaStore;

public class BasicMediaStore : IMediaStore
{
    public Task<string?> ResolveAsync(string gameRef, string assetKind)
    {
        return Task.FromResult<string?>(null);
    }
}
