# RetroBat Local API (APIExpose)

Cette API suit le cahier des charges "CDC" situé dans `docs/CDC.docx` et constitue la fondation de la version "MVP" vers "V1". 

## Structure 

Conformément à l'architecture C# recommandée, le code source a été organisé dans le répertoire `src/` :

- **RetroBat.Api** : Point d'entrée, expose le serveur sur le port `12345`.
  - Contrôleurs configurés : Health, Context, Version, Ingest (ES), Intent (pushView), Outputs (Mame), Rules, Media, Commands, Hiscores, Hub.
  - Déclare `Swagger UI` via Swashbuckle (accessible via HTTP au lancement).
  - Fournit le canal `WebSocket` (bidirectionnel persistant).
- **RetroBat.Domain** : Modèles canoniques purs (GameState, EventEnvelope, MameSignal), indépendants de l'implémentation.
- **RetroBat.Providers.EmulationStation** : Surveille ou sert d'ingest pour RetroBat/ES.
- **RetroBat.Providers.MameOutputs** : Écoute le flux temps réel sur le port TCP `8000` (outputs réseau), split les données et émet sur le bus d'événement interne.
- **RetroBat.Providers.Hi2Txt** : Implémente le pattern `FileSystemWatcher` sur `hi/` et `nvram/` pour actualiser le provider Hiscores.
- **RetroBat.MediaStore** : Interface permettant la future centralisation des LIP/LAY/XML.

## Architecture Temps Réel

Les différents `Providers` remontent l'information vers un `IEventBus` canonique (implémenté en `SimpleEventBus`).  
Ces informations sont automatiquement diffusées via `WebSocket` sur la route `/ws` depuis le `WebSocketConnectionManager`.

## Exécution

Si le .NET 8 SDK est installé via CLI :
```cmd
cd src\RetroBat.Api
dotnet run
``` 
L'API Swagger sera disponible et les contrôleurs REST et Websocket exposés sur `http://127.0.0.1:12345`.
Swagger UI te permet d'inspecter et tester toutes les requêtes directement depuis le navigateur via l'URL `http://127.0.0.1:12345/swagger`.

## Endpoints REST de l'API (v1)

Toutes les requêtes de base sont servies sur le port TCP `12345`, via la route `/api/v1/`.

### 1. Core & Diagnostics
- **`GET /api/v1/Health`**
  - Retourne l'état de santé du service local (`healthy`).
- **`GET /api/v1/Version`**
  - Retourne la version actuelle de l'API Expose.
- **`GET /api/v1/Context`**
  - Affiche l'état canonique complet (Mode Node, Infos UI/Frontend, Jeu sélectionné, Heure UTC).
- **`GET /api/v1/Context/state`**
  - Endpoint rapide pour obtenir uniquement l'état de jeu actuel (`state`, `selectedSystem`, `selectedGame`, `runningGame`). Les données sont enrichies, parsées et mises à jour en direct depuis `events.ini` !
- **`GET /api/v1/Context/current-game`**
  - Récupère les détails enrichis du jeu sélectionné ou lancé. Les données sont croisées avec `gamelist.xml` via l'API locale d'EmulationStation (exposée sur `127.0.0.1:1234`), permettant notamment d'avoir les données consolidées, l'ID d'un jeu (ex: son MD5), le résumé, l'image correspondante, etc.
- **`GET /api/v1/Context/current-system`**
  - Retourne les données détaillées du système actuellement en cours de navigation (y compris la configuration importée de `es_systems.cfg` et les paramètres issus de `es_settings.cfg` sous `<systemName>.*`).

### 2. Ingestion et Événements (EmulationStation / RetroBat)
- **Watcher local `events.ini`**
  - L'API lit et parse automatiquement en arrière-plan le fichier `C:\RetroBat\plugins\APIExpose\events.ini` (écrasé ou écrit par le frontend selon tes propres templates). Plus besoin d'un endpoint manuel, les événements `game-selected`, `system-selected`, `game-start`, `game-end` actualisent le State et le Websocket immédiatement.
- **`POST /api/v1/ingest/Es`**
  - Endpoint alternatif (JSON) pour pousser manuellement des événements si on ne passe pas par l'écriture du fichier `.ini`.

### 3. Ressources Média & Jeux
- **`GET /api/v1/Media/{assetPath}`**
  - Fournit les binaires ou images en utilisant le chemin défini (par exemple: un overlay, logo ou vidéo existant sur la borne).
### 4. HiScores & Achievements
- **`GET /api/v1/Hiscores?ids=...&md5=...`**
  - Renvoie le high score actuel lu et extrait via le module `hi2txt`. Les arguments d'url sont optionnels si le jeu est actuellement sélectionné ou lancé, le fallback sur l'état dynamique sera effectué automatiquement.

### 4. Moteur de Règles (LIP / LAY)
- **`GET /api/v1/Rules/active`**
  - Retourne la liste des configurations actives (LIP/LAY) selon le contexte de la machine.
- **`POST /api/v1/Rules/compile`**
  - Valide et compile le code des macros LIP ou de l'application MAME LAY en Intermediate Representation (IR).

### 5. Outputs & MAME
- **`GET /api/v1/Outputs/mame`**
  - Fournit un cliché du dernier état `Network Outputs` reçu (par ex. pour `target=chasehq`, signale l'état des lampes `genout`).
  - *Note additionnelle : l'API écoute en parallèle les outputs envoyés par l'émulateur sur le port source `8000` (`TCP listener`).*

### 6. Mode "Intent" et Hub (ArcadeHub / Mode Multi-Bornes)
- **`POST /api/v1/Intent/pushView`**
  - Force l'affichage d'une UI/d'un Layout spécifique avec priorité et Temps de Vie (TTL). (Payload de la scène attendu en body).
- **`POST /api/v1/Commands/launch`**
  - Demande le lancement d'un jeu précis (peut être invoqué par un autre client réseau après authentification).
- **`POST /api/v1/Hub/register` et `GET /api/v1/Hub/nodes`**
  - (Expérimental) Fédère la borne avec un Hub LAN partagé, et interroge l'état des autres bornes du réseau.

## Connexion Websocket (Événements Poussés)

Les clients UI dynamiques (Marquee MPV, Panneaux matrix LEDs, modules de notification) n'ont pas de polling lourd à effectuer.

`ws://127.0.0.1:12345/ws`

Ils peuvent se connecter à la socket permanente, et l'API RetroBat poussera automatiquement des `EventEnvelope` formatées en JSON dès qu'un changement majeur se produit, contenant par exemple :
- Changement de jeu (`ui.game.selected`)
- Evolution d'une lampe ou du coin door via MAME (`mame.output.changed`)
- Rafraîchissement d'un record local en temps réel (`hiscore.updated`)
