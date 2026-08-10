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
const params = new URLSearchParams(location.search);
// ?return : destination FINALE (le compte NelfePlay), fournie par l'approbation au
// retour. ?claimed : on revient de la page d'autorisation, il reste a retirer le
// credential. Tout se passe dans UN SEUL onglet, par redirections successives.
const RETURN = params.get('return');
const CLAIMED = params.has('claimed');
let timer = null;

// via=local : marque, pour l'approbation cote NelfePlay, que la demande vient de
// CETTE page locale — c'est ce qui la fera rediriger ici (?claimed) pour finaliser.
function withVia(u) { return u + (u.indexOf('?') === -1 ? '?' : '&') + 'via=local'; }
function goReturn() { if (RETURN && /^https?:\/\//.test(RETURN)) { location.replace(RETURN); } }

function render(state) {
  $('machine').textContent = state.label || '';
  if (state.status === 'linked') {
    $('title').textContent = 'Machine connectée';
    $('lead').innerHTML = '<span class="ok">Cette machine est reliée à votre compte.</span> ' +
      (RETURN ? 'Retour…' : 'Les jeux que vous ajoutez s’installeront tout seuls.');
    $('go').style.display = 'none';
    $('url').textContent = '';
    if (timer) { clearInterval(timer); timer = null; }
    setTimeout(goReturn, 600);
    return;
  }
  if (state.status === 'pending' && state.url) {
    // On repart (meme onglet) vers l'autorisation. L'adresse reste cliquable au cas
    // ou la redirection automatique n'aboutit pas.
    $('lead').textContent = 'Ouverture de la page d’autorisation…';
    $('go').style.display = 'none';
    $('url').innerHTML = 'Si rien ne s’ouvre : <a href="' + withVia(state.url) + '">continuer</a>';
    return;
  }
  if (state.status === 'expired') {
    $('lead').textContent = 'La demande a expiré. Relancez la connexion.';
    $('go').textContent = 'Connecter à NelfePlay'; $('go').style.display = ''; $('url').textContent = '';
  }
  if (state.status === 'error') {
    $('lead').textContent = 'NelfePlay est injoignable pour l’instant. Vérifiez la connexion, puis réessayez.';
    $('go').style.display = '';
  }
}

// Premiere visite : ouvre une demande et REDIRIGE (meme onglet) vers l'autorisation.
async function start() {
  $('go').disabled = true;
  try {
    const r = await fetch('/api/v1/nelfeplay/link/start', { method: 'POST' });
    const state = await r.json();
    if (state.status === 'linked') { render(state); return; }
    render(state);
    if (state.url) { location.href = withVia(state.url); }
  } catch { render({ status: 'error' }); } finally { $('go').disabled = false; }
}

// Retour d'autorisation : on RETIRE le credential (poll), puis on repart vers le compte.
async function claimLoop() {
  let tries = 0;
  const tick = async () => {
    let st = 'error';
    try { const r = await fetch('/api/v1/nelfeplay/link/state'); const s = await r.json(); render(s); st = s.status; } catch {}
    if (st === 'linked' || ++tries > 10) { if (timer) { clearInterval(timer); timer = null; } }
  };
  await tick();
  if (!timer) { timer = setInterval(tick, 1500); }
}

$('go').addEventListener('click', () => { CLAIMED ? claimLoop() : start(); });

(async () => {
  if (CLAIMED) { claimLoop(); return; }
  try {
    const r = await fetch('/api/v1/nelfeplay/link/state');
    const state = await r.json();
    if (state.status === 'linked') { render(state); return; }
    start();
  } catch { start(); }
})();
</script>
</body>
</html>
""";
}
