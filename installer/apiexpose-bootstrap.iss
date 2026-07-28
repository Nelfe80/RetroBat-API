; ─────────────────────────────────────────────────────────────────────────────
; apiexpose-bootstrap.iss — dépendance APIExpose partagée par les installeurs de
; plugins RetroBat (MarqueeManager, LedManager, RetroCreator, Fleet Hub…).
;
; #include-é depuis un installeur situé dans plugins\<Plugin>\installer\, il :
;   1. détecte APIExpose dans le dossier FRÈRE plugins\APIExpose ;
;   2. s'il manque, y installe le moteur embarqué (RetroBat.Api.exe + config) ;
;   3. lance son hook EmulationStation — comme le fait CabinetSetup.iss.
; Ainsi un installeur de plugin, à lui seul, laisse la borne prête. APIExpose déjà
; présent n'est jamais écrasé (mise à jour d'APIExpose = son propre installeur).
;
; Prérequis : {app} = le dossier du plugin (plugins\<Plugin>), donc son frère
; plugins\APIExpose est la cible du moteur. Les fichiers APIExpose sont référencés
; en ..\..\APIExpose\ (relatif à l'installeur, dans plugins\<Plugin>\installer\).
; ─────────────────────────────────────────────────────────────────────────────

[Files]
; Moteur APIExpose, copié UNIQUEMENT si le dossier frère n'a pas de RetroBat.Api.exe
Source: "..\..\APIExpose\RetroBat.Api.exe"; DestDir: "{code:ApiExposeDir}"; Flags: ignoreversion; Check: ApiExposeMissing
Source: "..\..\APIExpose\install-es-start-hook.bat"; DestDir: "{code:ApiExposeDir}"; Flags: ignoreversion; Check: ApiExposeMissing
; la configuration de la borne n'est jamais écrasée
Source: "..\..\APIExpose\appsettings.json"; DestDir: "{code:ApiExposeDir}"; Flags: onlyifdoesntexist uninsneveruninstall; Check: ApiExposeMissing

[Run]
; branche APIExpose dans EmulationStation, exactement comme son installeur dédié
Filename: "{code:ApiExposeDir}\install-es-start-hook.bat"; WorkingDir: "{code:ApiExposeDir}"; StatusMsg: "Installation d'APIExpose (hook EmulationStation)…"; Flags: runhidden; Check: ApiExposeMissing

[Code]
var
  ApiExposeChecked: Boolean;
  ApiExposeWasMissing: Boolean;

// plugins\<Plugin>  ->  plugins\APIExpose  (le dossier frère, sans "..")
function ApiExposeDir(Param: String): String;
begin
  Result := ExtractFilePath(RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) + 'APIExpose';
end;

// Vrai si APIExpose n'était PAS installé au démarrage de l'installation ; évalué
// une seule fois (avant toute copie), puis mémorisé pour [Files] et [Run].
function ApiExposeMissing(): Boolean;
begin
  if not ApiExposeChecked then
  begin
    ApiExposeWasMissing := not FileExists(ApiExposeDir('') + '\RetroBat.Api.exe');
    ApiExposeChecked := True;
  end;
  Result := ApiExposeWasMissing;
end;
