@echo off
setlocal
REM =====================================================================
REM  get-translatelocally.bat
REM  Telecharge le MOTEUR de traduction hors-ligne translateLocally ET
REM  des MODELES de langue (packs) dans ce dossier.
REM  Depuis APIExpose 1.7.2 l'installeur ne les bundle plus (gain ~40 Mo) ;
REM  ce script (ou le telechargement a la demande de l'API) les fournit.
REM
REM  Projet officiel : https://github.com/XapaJIaMnu/translateLocally
REM  Batch pur + curl.exe (pas de PowerShell).
REM =====================================================================

set "URL=https://github.com/XapaJIaMnu/translateLocally/releases/latest/download/translateLocally.windows-2019.x86-64.exe"
set "DEST=%~dp0translateLocally.windows-2019.x86-64.exe"

REM --- Modeles a installer (source anglais -> langue cible). Edite la liste. ---
set "MODELS=en-fr-tiny en-es-tiny en-de-tiny en-it-tiny en-pt-tiny"

echo ============================================================
echo  translateLocally : moteur + modeles
echo ============================================================

REM 1) Moteur
if exist "%DEST%" (
  echo [1/2] Moteur deja present.
) else (
  echo [1/2] Telechargement du moteur...
  curl.exe -L --fail --retry 3 -o "%DEST%" "%URL%"
  if errorlevel 1 (
    echo.
    echo [ERREUR] Moteur : telechargement echoue. Verifie ta connexion,
    echo ou renseigne une URL self-hostee en haut de ce script.
    del "%DEST%" 2>nul
    exit /b 1
  )
  echo   OK : "%DEST%"
)

REM 2) Modeles de langue (chaque -d telecharge un pack ; les paires
REM    indisponibles sont simplement ignorees).
echo [2/2] Modeles : %MODELS%
for %%m in (%MODELS%) do (
  echo   - %%m
  "%DEST%" -d %%m
)

echo.
echo Termine.
echo Les modeles telecharges seront consolides dans models\ au prochain
echo demarrage de l'API (store portable). Traduction hors-ligne prete.
endlocal
