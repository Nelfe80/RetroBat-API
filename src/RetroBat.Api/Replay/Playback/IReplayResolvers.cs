using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// Résout, sur CETTE machine, le core + la ROM d'un replay à partir de son manifeste.
/// Le <see cref="ReplayLaunchHint"/> local n'est qu'un accélérateur, jamais une condition.
/// </summary>
public interface IReplayRuntimeResolver
{
    ResolvedRuntime? Resolve(ReplayManifest manifest, ReplayLaunchHint? hint);
}

/// <summary>
/// ⭐ LA seam NelfeNet. Question posée par le lecteur avant de lancer : « rends-moi l'objet de ce
/// replay disponible localement ». Le lecteur n'a PAS à savoir d'où il vient.
///
/// Aujourd'hui une seule réponse possible (<see cref="LocalReplaySourceResolver"/> : il est là, ou
/// il n'est pas là). Demain, une implémentation NelfeNet pourra le télécharger depuis une autre
/// borne / un relais avant de rendre la main — sans toucher une ligne du lecteur.
/// </summary>
public interface IReplaySourceResolver
{
    /// <summary>Vrai si l'objet est (devenu) disponible localement. L'implémentation peut le
    /// récupérer ; elle NE valide PAS l'intégrité (c'est R6, fait juste après par le lecteur).</summary>
    Task<bool> EnsureObjectAvailableAsync(ReplayManifest manifest, CancellationToken ct);
}

/// <summary>
/// Implémentation LOCALE : l'objet est disponible s'il est déjà dans le magasin. Aucun réseau,
/// aucune récupération — c'est le comportement d'avant NelfeNet, rendu explicite.
/// </summary>
public sealed class LocalReplaySourceResolver : IReplaySourceResolver
{
    private readonly IReplayObjectStore _objects;

    public LocalReplaySourceResolver(IReplayObjectStore objects) => _objects = objects;

    public Task<bool> EnsureObjectAvailableAsync(ReplayManifest manifest, CancellationToken ct)
        => Task.FromResult(File.Exists(_objects.ObjectPath(manifest.Object.Sha256)));
}
