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
  <a class="b" id="account" href="#" style="display:none">Voir mes machines</a>
  <div class="url" id="url"></div>
</div>
<script>
const $ = (id) => document.getElementById(id);
const params = new URLSearchParams(location.search);
// ?return : destination FINALE (le compte), fournie par l'approbation au retour.
// ?claimed : on revient de la page d'autorisation, il reste a retirer le credential.
// ?lang : la langue d'origine (le site qui a lance l'appairage). Tout se passe dans
// UN SEUL onglet, par redirections successives.
const RETURN = params.get('return');
const CLAIMED = params.has('claimed');
const SITE = 'https://nelfeplay.com';
const LANG = (((params.get('lang') || navigator.language || 'fr').slice(0, 2).toLowerCase()) === 'en') ? 'en' : 'fr';
const T = {
  fr: {
    connectTitle: 'Connecter cette machine',
    connectLead: 'Un clic ici, un clic sur NelfePlay, et c’est fait. Aucun code à recopier.',
    go: 'Connecter à NelfePlay',
    opening: 'Ouverture de la page d’autorisation…',
    ifNothing: 'Si rien ne s’ouvre : ', cont: 'continuer',
    linkedTitle: 'Machine connectée',
    linkedOk: 'Cette machine est reliée à votre compte NelfePlay.',
    linkedInstall: 'Les jeux et démos que vous ajouterez à votre gamelist pourront être automatiquement installés sur cette machine.',
    expired: 'La demande a expiré. Relancez la connexion.',
    error: 'NelfePlay est injoignable pour l’instant. Vérifiez la connexion, puis réessayez.',
    seeAccount: 'Voir mes machines',
  },
  en: {
    connectTitle: 'Connect this machine',
    connectLead: 'One click here, one click on NelfePlay, and you’re done. No code to copy.',
    go: 'Connect to NelfePlay',
    opening: 'Opening the authorisation page…',
    ifNothing: 'If nothing opens: ', cont: 'continue',
    linkedTitle: 'Machine connected',
    linkedOk: 'This machine is linked to your NelfePlay account.',
    linkedInstall: 'Games and demos you add to your gamelist can be installed on this machine automatically.',
    expired: 'The request has expired. Start the connection again.',
    error: 'NelfePlay is unreachable right now. Check your connection and try again.',
    seeAccount: 'See my machines',
  },
}[LANG];
// Destination finale : le return fourni par l'approbation, sinon le compte, LOCALISE —
// pour ne jamais rester bloque sur cette page, meme quand la machine est deja liee.
const ACCOUNT = (RETURN && /^https?:\/\//.test(RETURN)) ? RETURN : (SITE + '/' + LANG + '/account#mymachines');
let timer = null;

// via=local : marque, pour NelfePlay, que la demande vient de CETTE page locale — ce qui
// fait auto-autoriser puis rediriger ici (?claimed) pour finaliser.
function withVia(u) { return u + (u.indexOf('?') === -1 ? '?' : '&') + 'via=local'; }
// Le lien d'autorisation est fige sur /fr/ cote serveur : on l'aligne sur la langue d'origine.
function localize(u) { return u.replace(/\/(fr|en)\/link\//, '/' + LANG + '/link/'); }

document.documentElement.lang = LANG;
$('title').textContent = T.connectTitle;
$('lead').textContent = T.connectLead;
$('go').textContent = T.go;
$('account').textContent = T.seeAccount;

function render(state) {
  $('machine').textContent = state.label || '';
  if (state.status === 'linked') {
    $('title').textContent = T.linkedTitle;
    $('lead').innerHTML = '<span class="ok">' + T.linkedOk + '</span> ' + T.linkedInstall;
    $('go').style.display = 'none';
    $('url').textContent = '';
    $('account').href = ACCOUNT; $('account').style.display = '';
    if (timer) { clearInterval(timer); timer = null; }
    // Redirection AUTO vers le compte (le bouton reste en secours si bloquee).
    setTimeout(() => { location.replace(ACCOUNT); }, 1200);
    return;
  }
  if (state.status === 'pending' && state.url) {
    $('lead').textContent = T.opening;
    $('go').style.display = 'none';
    $('url').innerHTML = T.ifNothing + '<a href="' + withVia(localize(state.url)) + '">' + T.cont + '</a>';
    return;
  }
  if (state.status === 'expired') {
    $('lead').textContent = T.expired; $('go').textContent = T.go; $('go').style.display = ''; $('url').textContent = '';
  }
  if (state.status === 'error') {
    $('lead').textContent = T.error; $('go').style.display = '';
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
    if (state.url) { location.href = withVia(localize(state.url)); }
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
