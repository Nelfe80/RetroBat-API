translateLocally tool folder / Dossier outil translateLocally
=============================================================

EN
--
Purpose:
translateLocally is an optional offline translation helper used to translate
game descriptions. Since APIExpose 1.7.2 the engine (~40 MB) is NOT bundled in
the installer — provide it in ONE of these ways:

A. Run get-translatelocally.bat (in this folder). It downloads the engine here.
B. Let the API fetch it on demand: set
   Scraping.TranslateLocallyEngineDownloadUrl (appsettings) to a portable-engine
   URL; the API downloads it into this folder on first translation.
C. Manually: download from the official project
   https://github.com/XapaJIaMnu/translateLocally
   and place translateLocally.windows-2019.x86-64.exe directly in this folder
   (NOT in a bin subfolder).

Notes:
- Expected executable path:
  APIExpose/tools/translateLocally/translateLocally.windows-2019.x86-64.exe
- Language models download automatically on first translation (into models\).
- If the engine is absent, translation degrades gracefully; nothing else breaks.
- Git ignores the application, models and caches. Only README.txt and
  get-translatelocally.bat are tracked.

FR
--
Role :
translateLocally est un helper de traduction hors-ligne optionnel, utilise pour
traduire les descriptions de jeux. Depuis APIExpose 1.7.2 le moteur (~40 Mo)
n'est PLUS bundle dans l'installeur — fournis-le d'UNE de ces facons :

A. Lance get-translatelocally.bat (dans ce dossier). Il telecharge le moteur ici.
B. Laisse l'API le telecharger a la demande : renseigne
   Scraping.TranslateLocallyEngineDownloadUrl (appsettings) avec une URL de
   moteur portable ; l'API le telecharge ici a la premiere traduction.
C. Manuellement : depuis le projet officiel
   https://github.com/XapaJIaMnu/translateLocally
   place translateLocally.windows-2019.x86-64.exe directement dans ce dossier
   (PAS dans un sous-dossier bin).

Notes :
- Chemin attendu de l'executable :
  APIExpose/tools/translateLocally/translateLocally.windows-2019.x86-64.exe
- Les modeles de langue se telechargent automatiquement a la premiere
  traduction (dans models\).
- Si le moteur est absent, la traduction se degrade proprement ; rien d'autre
  n'est casse.
- Git ignore l'application, les modeles et les caches. Seuls README.txt et
  get-translatelocally.bat sont suivis.
