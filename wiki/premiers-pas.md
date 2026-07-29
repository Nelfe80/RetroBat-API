# Premiers pas

Installer APIExpose tient en un **installateur** : on télécharge, on lance, on active.

## Avant de commencer

- une installation **RetroBat** fonctionnelle ;
- le **[runtime .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0)** ;
- une sauvegarde de votre dossier RetroBat si votre installation compte pour vous — APIExpose modifie gamelists et réglages.

## Installation

1. Téléchargez **[`APIExpose-Cabinet-Setup.exe`](https://github.com/Nelfe80/RetroBat-APIExpose/releases/latest/download/APIExpose-Cabinet-Setup.exe)** depuis la page des releases — il contient le programme, les outils (ffmpeg, ImageMagick, translateLocally) et le Data Pack complet.
2. Lancez l'installateur : il installe le plugin dans `RetroBat\plugins\` et enregistre le hook de démarrage EmulationStation — vous obtenez :

    ```text
    RetroBat\plugins\APIExpose\
    ```

3. Relancez RetroBat normalement : APIExpose démarre automatiquement, fait ses traitements de démarrage, puis laisse EmulationStation continuer.

!!! note "Que fait le hook ?"
    L'installateur enregistre uniquement ce script côté EmulationStation, sans modifier `updatestores.bat` ni le reste de RetroBat :

    ```text
    emulationstation\.emulationstation\scripts\start\APIExpose-start-wait.bat
    ```

## Vérifier que ça marche

APIExpose lancé, ouvrez dans un navigateur :

```text
http://127.0.0.1:12345/api/v1/health
```

Réponse attendue :

```json
{ "status": "healthy", "version": "1.0.0+..." }
```

L'état du démarrage est sur `/api/v1/startup/ready`, et la liste complète des endpoints sur `http://127.0.0.1:12345/swagger/index.html`.

## Vos réglages, directement dans RetroBat

APIExpose ajoute ses options **dans les menus d'EmulationStation**, traduites — pas de fichier à éditer pour l'usage courant :

```text
EXTENDED OPTIONS
API SETTINGS
AUTO SCRAPING MANAGER
LOCAL MEDIA MANAGER
ROMS PACK MANAGER
THEMES MANAGER
COLLECTIONS PACK MANAGER
```

## Arrêter ou désinstaller

| Action | Comment |
|---|---|
| Arrêter APIExpose | Double-clic sur `stop.bat` |
| Retirer le lancement automatique | Double-clic sur `uninstall-es-start-hook.bat` |

Les fichiers restent dans `plugins\APIExpose` — relancer le hook réactive tout.

!!! tip "Conseils simples"
    Installez le hook une seule fois, puis lancez RetroBat normalement. Ne supprimez pas les dossiers `media\`, `resources\`, `tools\` ou `wrapper\` : APIExpose en a besoin.
