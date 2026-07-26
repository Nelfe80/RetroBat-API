using System.Runtime.Versioning;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// La page qui connecte cette machine a un compte, sans code a recopier.
///
/// Elle est servie par APIExpose lui-meme, ce qui n'est pas un detail : une
/// page de MEME ORIGINE a le droit d'ecrire sur cette API, la ou une page
/// venue d'ailleurs ne l'a pas. Le joueur ouvre une adresse locale, clique une
/// fois, et se retrouve sur nelfeplay.com pour accorder.
///
/// Pas de QR : sur un poste, il y a un navigateur. En salle, c'est le hub qui
/// appaire les bornes depuis son interface — il les joint deja.
/// </summary>
[ApiController]
[Tags("NelfePlay")]
[SupportedOSPlatform("windows")]
public sealed class NelfePlayLinkController : ControllerBase
{
    private readonly NelfePlayLinkService _links;

    public NelfePlayLinkController(NelfePlayLinkService links)
    {
        _links = links;
    }

    /// <summary>La page de connexion, en HTML.</summary>
    [HttpGet("/link")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ContentResult Page()
    {
        return Content(Html(), "text/html", Encoding.UTF8);
    }

    /// <summary>Ouvre une demande et rend l'adresse a ouvrir.</summary>
    [HttpPost("api/v1/nelfeplay/link/start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Start(CancellationToken cancellationToken)
        => Ok(await _links.StartAsync(cancellationToken));

    /// <summary>Regarde si l'accord est venu.</summary>
    [HttpGet("api/v1/nelfeplay/link/state")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> State(CancellationToken cancellationToken)
        => Ok(await _links.PollAsync(cancellationToken));

    /// <summary>
    /// La page, en dur.
    ///
    /// Aucun fichier a deployer, aucune ressource exterieure a charger : cette
    /// page doit fonctionner sur une machine sans internet au moment ou l'on
    /// cherche justement a savoir pourquoi elle n'est pas connectee.
    /// </summary>
    private static string Html() => """
<!DOCTYPE html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Connecter cette machine — NelfePlay</title>
<style>
  :root { color-scheme: dark; }
  body { margin:0; min-height:100dvh; display:flex; align-items:center; justify-content:center;
         background:radial-gradient(ellipse 80% 60% at 50% -10%,#0d2230 0%,#050913 55%),#050913;
         font-family:'Segoe UI',system-ui,sans-serif; color:#f5f7fb; padding:24px; }
  .box { max-width:460px; width:100%; text-align:center; }
  h1 { font-size:1.6rem; margin:0 0 10px; }
  p { color:#8f9bb0; line-height:1.6; margin:0 0 22px; }
  .machine { padding:14px 16px; border:1px solid rgba(151,174,211,.28); border-radius:12px;
             background:rgba(8,12,20,.6); font-size:1.2rem; font-weight:800; margin-bottom:22px; }
  button, a.b { display:inline-block; background:#32d8ed; color:#04070d; font-weight:800;
                text-decoration:none; border:0; padding:14px 28px; border-radius:12px;
                font-size:1rem; cursor:pointer; }
  button[disabled] { opacity:.5; cursor:default; }
  .url { margin-top:18px; font-size:.85rem; color:#8f9bb0; word-break:break-all; }
  .ok { color:#5ce3a1; font-weight:700; }
</style>
</head>
<body>
<div class="box">
  <h1 id="title">Connecter cette machine</h1>
  <div class="machine" id="machine">…</div>
  <p id="lead">Un clic ici, un clic sur NelfePlay, et c'est fait. Aucun code à recopier.</p>
  <button id="go">Connecter à NelfePlay</button>
  <div class="url" id="url"></div>
</div>
<script>
const $ = (id) => document.getElementById(id);
let timer = null;

function render(state) {
  $('machine').textContent = state.label || '';
  if (state.status === 'linked') {
    $('title').textContent = 'Machine connectée';
    $('lead').innerHTML = '<span class="ok">Cette machine est reliée à votre compte.</span> ' +
      'Les jeux que vous ajoutez s’installeront tout seuls.';
    $('go').style.display = 'none';
    $('url').textContent = '';
    if (timer) { clearInterval(timer); timer = null; }
    return;
  }
  if (state.status === 'pending' && state.url) {
    $('lead').textContent = 'Autorisez cette machine dans la page qui vient de s’ouvrir, puis revenez ici.';
    $('go').textContent = 'Rouvrir la page d’autorisation';
    // L'adresse reste VISIBLE : si l'ouverture automatique echoue — navigateur
    // absent, fenetre bloquee — le joueur doit pouvoir la lire et la reporter.
    $('url').innerHTML = 'Ou ouvrez : <a href="' + state.url + '" target="_blank" rel="noopener">' + state.url + '</a>';
    return;
  }
  if (state.status === 'expired') {
    $('lead').textContent = 'La demande a expiré. Relancez la connexion.';
    $('go').textContent = 'Connecter à NelfePlay';
    $('url').textContent = '';
  }
  if (state.status === 'error') {
    $('lead').textContent = 'NelfePlay est injoignable pour l’instant. Vérifiez la connexion, puis réessayez.';
  }
}

async function start() {
  $('go').disabled = true;
  try {
    const r = await fetch('/api/v1/nelfeplay/link/start', { method: 'POST' });
    const state = await r.json();
    render(state);
    if (state.url) { window.open(state.url, '_blank', 'noopener'); }
    if (!timer) { timer = setInterval(poll, 3000); }
  } finally {
    $('go').disabled = false;
  }
}

async function poll() {
  const r = await fetch('/api/v1/nelfeplay/link/state');
  render(await r.json());
}

$('go').addEventListener('click', start);
poll();
</script>
</body>
</html>
""";
}
