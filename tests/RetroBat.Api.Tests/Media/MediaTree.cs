namespace RetroBat.Api.Tests.Media;

/// <summary>
/// A throwaway media tree under the OS temp dir — deliberately OUTSIDE the RetroBat
/// roots, so <c>CreateAsset</c> relativises nothing and the stored <c>Path</c> stays a
/// bare file name, keeping golden assertions predictable. Disposable: deletes itself.
/// </summary>
internal sealed class MediaTree : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "hotpath-golden-" + Guid.NewGuid().ToString("N"));

    public MediaTree() => Directory.CreateDirectory(Root);

    /// <summary>Writes a file at a '/'-separated path relative to the tree root.</summary>
    public MediaTree With(string relative, string content = "x")
    {
        var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort cleanup */ }
    }
}
