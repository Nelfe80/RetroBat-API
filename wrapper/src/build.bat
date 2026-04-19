@echo off
echo =======================================
echo Building Arcade Wrapper DLL pour RetroBat
echo =======================================

echo 1. Telechargement de libretro.h (API standard)...
if not exist "libretro.h" (
    curl -s -o libretro.h https://raw.githubusercontent.com/libretro/RetroArch/master/libretro-common/include/libretro.h
    echo - libretro.h telecharge !
) else (
    echo - libretro.h deja present.
)

echo.
echo 2. Compilation des ressources et de la DLL...
rc.exe wrapper.rc
cl.exe /O2 /EHsc /LD wrapper.cpp /DEF:wrapper.def /link wrapper.res /OUT:wrapper.dll

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERREUR : La compilation a echoue.
    echo Assure-toi d'executer ce script depuis le "x64 Native Tools Command Prompt" ou "x86".
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo SUCCES! Le wrapper a ete cree : wrapper.dll
echo.
echo == Pour l'installer : ==
echo 1. Renomme "wrapper.dll" avec le nom de ton emulation cible (Ex: genesis_plus_gx_libretro.dll)
echo 2. Renomme le vrai emulateur avec "_real" a la fin (Ex: genesis_plus_gx_libretro_real.dll)
pause
