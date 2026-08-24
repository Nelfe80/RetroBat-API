[Code]
// apiexpose-bootstrap.iss - DÉTECTION d'APIExpose, partagée par les installeurs de
// plugins RetroBat (MarqueeManager, LedManager, RetroCreator, Fleet Hub…).
//
// Fournit ApiExposeInstalled() : vrai si le dossier FRÈRE plugins\APIExpose contient
// RetroBat.Api.exe. L'installeur qui l'#include AVERTIT si APIExpose manque (dans son
// propre code) et invite à lancer APIExpose-Cabinet-Setup - la dépendance n'est PAS
// bundlée (APIExpose = dossier complet + Data Pack, installé par son propre setup).
//
// IMPORTANT : ce fichier commence DIRECTEMENT par [Code] et n'utilise que des
// commentaires « // » (il peut être #inclus après un autre include finissant en
// [Code], où un « ; » serait lu comme du Pascal).

// plugins\<Plugin>  ->  plugins\APIExpose  (le dossier frère, sans "..")
function ApiExposeDir(Param: String): String;
begin
  Result := ExtractFilePath(RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) + 'APIExpose';
end;

// Vrai si APIExpose est déjà installé à côté (RetroBat.Api.exe présent).
function ApiExposeInstalled(): Boolean;
begin
  Result := FileExists(ApiExposeDir('') + '\RetroBat.Api.exe');
end;
