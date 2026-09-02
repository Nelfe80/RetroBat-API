using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>
/// Page locale « regarder ce replay » — servie par APIExpose lui-même.
///
/// Pourquoi une page et pas un fetch depuis nelfeplay.com : les navigateurs
/// bloquent désormais une page publique (HTTPS) qui tente de JOINDRE le loopback
/// (Local Network Access). Une NAVIGATION vers le loopback, elle, reste
/// autorisée. Le bouton « ▷ Replay » du site NAVIGUE donc ici ; cette page, de
/// MÊME ORIGINE que l'API, a le droit de la solliciter : elle POST le play en
/// same-origin (jamais bloqué), puis ramène le visiteur sur le site.
///
/// Même patron que /link (appairage). Aucune ressource externe : la page doit
/// marcher sur une borne sans Internet.
/// </summary>
[ApiController]
[Tags("Replay")]
public sealed class ReplayWatchController : ControllerBase
{
    // Hôtes autorisés pour le retour (anti open-redirect) : les mêmes que la CORS.
    private static bool IsAllowedReturnHost(string host) =>
        host.Equals("nelfeplay.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".nelfeplay.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("nelfetech.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".nelfetech.com", StringComparison.OrdinalIgnoreCase);

    [HttpGet("/replay/watch")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ContentResult Watch(
        [FromQuery(Name = "replay_id")] string? replayId,
        [FromQuery(Name = "return")] string? returnUrl)
    {
        return Content(Html(SanitizeReplayId(replayId), SafeReturn(returnUrl)), "text/html", Encoding.UTF8);
    }

    // Un id de replay est « rp_ » + base32 : on n'accepte que ça (defense en
    // profondeur, même si la valeur est ensuite encodée en littéral JS).
    private static string SanitizeReplayId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        foreach (var c in id)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';
            if (!ok) return "";
        }
        return id.Length <= 64 ? id : "";
    }

    // Le retour doit viser le site (https, hôte connu). Sinon on retombe sur la
    // racine publique. Empêche qu'un lien forgé renvoie ailleurs.
    private static string SafeReturn(string? url)
    {
        const string fallback = "https://nelfeplay.com/";
        if (string.IsNullOrWhiteSpace(url)) return fallback;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return fallback;
        if (u.Scheme != Uri.UriSchemeHttps) return fallback;
        return IsAllowedReturnHost(u.Host) ? u.GetLeftPart(UriPartial.Query) : fallback;
    }

    private static string Html(string replayId, string returnUrl)
    {
        // Valeurs injectées en littéraux JS sûrs (JsonSerializer échappe tout).
        var idJs = JsonSerializer.Serialize(replayId);
        var retJs = JsonSerializer.Serialize(returnUrl);

        return $$"""
<!DOCTYPE html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>Replay — NelfePlay</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
         background:#060811; color:#f5f7fb; font:16px/1.5 system-ui,Segoe UI,Roboto,sans-serif; }
  .card { width:min(92vw,480px); padding:34px 30px; text-align:center;
          background:#0d1120; border:1px solid #23283a; border-radius:18px;
          box-shadow:0 20px 60px rgba(0,0,0,.45); }
  .mark { font-weight:800; letter-spacing:.02em; color:#A98BFF; margin-bottom:18px; }
  h1 { font-size:1.4rem; margin:.2em 0 .1em; }
  p { color:#ccd5e5; margin:.4em 0; }
  .spin { width:38px; height:38px; margin:14px auto; border-radius:50%;
          border:3px solid #2a3350; border-top-color:#A98BFF; animation:s .8s linear infinite; }
  @keyframes s { to { transform:rotate(360deg); } }
  @media (prefers-reduced-motion:reduce){ .spin{ animation:none; } }
  .ok .spin, .err .spin { display:none; }
  .badge { font-size:2.2rem; margin:6px 0; }
  a.btn { display:inline-block; margin-top:18px; padding:11px 20px; border-radius:12px;
          text-decoration:none; font-weight:600; color:#fff; background:#5B34D6; }
  a.btn:hover { background:#6b45e6; }
  .muted { font-size:.85rem; color:#8a93a8; }
</style>
</head>
<body>
  <main class="card" id="card">
    <div class="mark">Nelfe<span style="color:#fff">Play</span></div>
    <div class="spin" aria-hidden="true"></div>
    <h1 id="title">Lancement du replay…</h1>
    <p id="msg">Un instant, la borne prépare la lecture.</p>
    <a class="btn" id="back" href="#" hidden>Revenir au site</a>
    <p class="muted" id="hint" hidden>Retour automatique dans quelques secondes.</p>
  </main>
<script>
(function(){
  var ID = {{idJs}}, RET = {{retJs}};
  var card = document.getElementById('card'), title = document.getElementById('title'),
      msg = document.getElementById('msg'), back = document.getElementById('back'),
      hint = document.getElementById('hint');
  back.setAttribute('href', RET);

  function show(cls, badge, t, m, auto){
    card.className = 'card ' + cls;
    title.textContent = t; msg.innerHTML = '';
    if (badge){ var b=document.createElement('div'); b.className='badge'; b.textContent=badge; msg.appendChild(b); }
    var mm=document.createElement('span'); mm.textContent=m; msg.appendChild(mm);
    back.hidden = false;
    if (auto){ hint.hidden = false; setTimeout(function(){ location.href = RET; }, 3200); }
  }

  if (!ID){ show('err','⚠️','Replay introuvable','Aucun identifiant de replay fourni.', false); return; }

  // POST same-origin : jamais soumis au blocage Local Network Access.
  fetch('/api/v1/replay/play', {
    method:'POST', headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ replay_id: ID }), cache:'no-store'
  }).then(function(r){
    if (r.status === 200) { show('ok','▶','Lecture sur la borne','Le replay se joue sur l’écran de la borne.', true); }
    else if (r.status === 404) { show('err','🔎','Pas disponible ici','Ce replay n’est pas présent sur cette borne.', false); }
    else if (r.status === 409) { show('err','⏳','Déjà en cours','Une lecture est déjà en cours sur la borne.', false); }
    else { show('err','⚠️','Impossible de lancer','La borne a refusé la lecture (code ' + r.status + ').', false); }
  }).catch(function(){
    show('err','⚠️','Borne injoignable','Impossible de contacter APIExpose sur cette machine.', false);
  });
})();
</script>
</body>
</html>
""";
    }
}
