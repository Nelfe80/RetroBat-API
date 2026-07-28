[Code]
// obs-check.iss — détection d'OBS Studio, partagée par les installeurs qui en
// dépendent (RetroCreator, Fleet Hub…). Fournit UNIQUEMENT la fonction
// ObsInstalled() ; l'installeur qui l'#include décide quoi en faire (avertir dans
// son propre code). OBS n'est jamais installé automatiquement (appli tierce
// lourde) : on informe l'utilisateur s'il manque.
//
// IMPORTANT : ce fichier commence DIRECTEMENT par [Code] et n'utilise que des
// commentaires « // » — il est #inclus après apiexpose-bootstrap.iss (qui finit
// en [Code]), donc tout commentaire « ; » ici serait lu comme du Pascal.

// Vrai si OBS Studio est détecté (clé de désinstallation ou exe au chemin par défaut).
function ObsInstalled(): Boolean;
var
  s: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OBS Studio', 'DisplayName', s) or
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\OBS Studio', 'DisplayName', s) or
    FileExists(ExpandConstant('{commonpf64}\obs-studio\bin\64bit\obs64.exe')) or
    FileExists(ExpandConstant('{commonpf32}\obs-studio\bin\64bit\obs64.exe')) or
    FileExists(ExpandConstant('{commonpf}\obs-studio\bin\64bit\obs64.exe'));
end;
