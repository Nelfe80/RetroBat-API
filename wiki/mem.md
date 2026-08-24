# Créer ses fichiers .MEM

Un fichier `.MEM` apprend à APIExpose à **lire la mémoire d'un jeu pendant que vous y jouez** : où se trouvent les vies, le score, l'état du personnage… C'est grâce à lui que vos LEDs flashent quand Sonic perd ses rings et que le score s'affiche en direct sur le marquee - sans modifier ni le jeu ni l'émulateur.

Cette page vous apprend à écrire le vôtre. Aucun outil spécial requis : un `.MEM` est un simple fichier texte.

!!! tip "Partagez votre .MEM avec la communauté"
    Vous avez un `.MEM` qui marche pour un jeu ? **Proposez-le sur NelfePlay** : il est validé, testé, puis - s'il est bon - intégré au dossier RAM officiel et déployé sur toutes les bornes, crédité à votre nom.
    → **[Contribuer un .MEM](https://nelfeplay.com/fr/mem/contribute)** (compte NelfePlay requis).

## Comment ça marche

```text
RetroArch exécute le jeu
   → le wrapper APIExpose lit la RAM décrite par le .MEM
      → les changements deviennent des événements (« Player loses a life »)
         → LedManager, MarqueeManager et vos outils y réagissent
```

## Trouver les adresses : MEM Explorer

Écrire un `.MEM` demande de connaître **où** le jeu range ses vies, son score, son état. **MEM Explorer** est l'outil de bureau qui les découvre pour vous, installé avec APIExpose :

```text
plugins\APIExpose\tools\mem-explorer\MemExplorer.exe
```

Il lance un jeu **via APIExpose**, observe la mémoire pendant que vous jouez et vous aide à isoler l'adresse qui change quand la valeur change à l'écran. Deux moteurs selon la plateforme, choisis automatiquement : le **wrapper RetroArch** pour les consoles, le **pont Lua MAME** pour l'arcade.

La méthode est celle de tous les chercheurs d'adresses : jouez, dites à l'outil ce que vous venez d'observer (« j'ai perdu une vie », « le score a augmenté »), et la liste des adresses candidates se réduit à chaque passe jusqu'à la bonne.

L'outil écrit ensuite la définition **au format du curator** - le même vocabulaire que les `.MEM` officiels - et vous pouvez la tester en jeu immédiatement, avant de la proposer à la communauté.

!!! note "Le mode découverte ne tourne que quand vous le demandez"
    Lire la RAM en continu coûte du temps machine. Le mode découverte est activé par MEM Explorer le temps de la session, puis relâché : une borne qui joue normalement n'en paye rien.

## Où placer le fichier

```text
plugins\APIExpose\resources\ram\<système>\<nom-du-jeu>.MEM
```

Par exemple `resources\ram\nes\super-mario-bros.MEM`. Le `<système>` est le **nom du dossier RetroBat** (`nes`, `snes`, `megadrive`, `mame`…). Regardez les fichiers existants du système pour suivre le même style de nommage. Un fichier `alias.json` dans le dossier système peut faire pointer plusieurs noms de ROM (régions, romhacks) vers le même `.MEM`.

!!! tip "Partez d'un existant"
    Le Data Pack contient déjà des milliers de `.MEM`. Ouvrez celui d'un jeu proche du vôtre : c'est le meilleur modèle de départ.

## La structure : trois blocs

Un `.MEM` est une table Lua avec trois sections, toujours dans cet ordre :

```lua
return {
  game = { ... },      -- qui est ce jeu
  rom = { ... },       -- quelles ROMs correspondent
  events = { ... }     -- les événements surveillés en jeu
}
```

### 1. `game` - l'identité du jeu

```lua
game = {
  title = "Super Mario Bros.",
  system = "nes",                 -- nom du dossier RetroBat, obligatoire
  system_name = "NES/Famicom",    -- nom lisible, recommandé
  game_id = 1446                  -- id base de données, optionnel
}
```

### 2. `rom` - les ROMs compatibles

```lua
rom = {
  name = "super-mario-bros",      -- kebab-case minuscules = nom du fichier .MEM
  hashes = {
    { hash = "8e3630186e35d477231bf8fd50e54cdd",
      label = "Super Mario Bros. (World).nes",
      tags = { "nointro" } }
  }
}
```

Les hashes permettent de reconnaître les variantes (régions, versions) sans dupliquer le fichier ; `alias.json` dans le dossier système fait le lien nom de ROM/hash → fichier `.MEM`.

**Où trouver les adresses ?** Avec le cheat engine de RetroArch, un débogueur d'émulateur, ou les bases communautaires (Data Crystal, guides de romhacking). Les adresses des cheat codes existants sont souvent un excellent point de départ.

### 3. `events` - ce qui déclenche les effets

Un événement = une adresse + un type + une **condition** + une **action** de la nomenclature + une description, rangé dans sa famille `categorie.sous_famille` :

```lua
events = {
  resources = {
    lives = {
      { address=0X75A, type="u8", condition="decrease", action="LOSE_LIFE", desc="Player loses a life" }
    }
  },
  scoring = {
    points = {
      { address=0X840, type="u24be", condition="change", action="SCORE_STATE", desc="Score" }
    }
  }
}
```

## Les types

| Type | Signification |
|---|---|
| `u8` | 1 octet - **le choix par défaut** |
| `u16le` / `u16be` | 2 octets, little / big endian |
| `u24le` / `u24be` | 3 octets (fréquent pour les scores) |
| `u32le` / `u32be` | 4 octets |

Seuls ces sept types non signés sont reconnus par le runtime ; tout autre type est lu comme `u8`.

Ne devinez pas l'endianness d'une valeur multi-octets : en cas de doute, restez en `u8`.

## Les conditions

| Condition | Quand l'utiliser | Exemples |
|---|---|---|
| `decrease` | La valeur baisse de façon signifiante | vies, santé, timer, munitions |
| `increase` | La valeur monte de façon signifiante | score, rings, expérience, combo |
| `change` | Ça change, sans direction particulière | niveau courant, salle, état du joueur |
| `equal` | Une valeur précise est atteinte (avec `min`/`max`) | écran titre actif, invincibilité, boss vaincu |
| `any` | Dernier recours, observation non directionnelle | |

## Les huit familles d'événements

Chaque événement se range dans une famille, avec des sous-clés normalisées (minuscules, `snake_case`) :

| Famille | Contenu | Sous-clés typiques |
|---|---|---|
| `flow` | Où en est le jeu | `title_screen`, `in_game`, `pause`, `game_over`, `credits` |
| `progression` | L'avancement | `world`, `level`, `stage`, `room`, `lap`, `checkpoint` |
| `resources` | Ce qui se gagne/perd | `lives`, `health`, `ammo`, `oxygen`, `timer` |
| `inventory` | Les objets | `items`, `keys`, `weapon`, `held_object` |
| `combat` | Les affrontements | `boss_hit`, `damage_taken`, `enemy_state` |
| `scoring` | La performance | `score`, `coins_rings`, `currency`, `experience`, `combo` |
| `state` | Formes et effets | `player_state`, `powerup_state`, `temporary_state`, `status_effect` |
| `system` | Le technique utile | `memory`, `prng`, `flags` |

Utilisez les noms canoniques : `rings` et `coins` deviennent `coins_rings`, `gold`/`rupees` deviennent `currency`, `XP` devient `experience`. Une valeur valide mais inclassable va dans `system.memory` - on ne jette rien.

## Nommer ce que le joueur a en main

Deux actions se lisent autrement que les autres : leur `desc` porte **un nom**, pas une description.

| Action | Famille | Ce que `desc` contient |
|---|---|---|
| `CHARACTER_SELECTED` | `state.player` | le personnage joué - `"Cody"`, `"Ryu"` |
| `WEAPON_SELECTED` | `inventory.weapon` | l'arme en main - `"Fire Water"`, `"Shotgun"` |

C'est ce qui permet à une carte d'instructions de s'afficher toute seule : le jeu annonce *Cody*, l'écran montre la fiche de Cody.

Une entrée par valeur, en `condition="eq"` - elle ne se déclenche qu'à **l'entrée** dans la valeur, donc une fois au moment du choix :

```lua
state = {
  player = {
    { address=0X857D, type="u8", condition="eq", value=0X00, action="CHARACTER_SELECTED", player=1, desc="Guy" },
    { address=0X857D, type="u8", condition="eq", value=0X01, action="CHARACTER_SELECTED", player=1, desc="Cody" },
    { address=0X857D, type="u8", condition="eq", value=0X02, action="CHARACTER_SELECTED", player=1, desc="Haggar" },
  },
},
```

Deux règles rendent ces entrées utilisables :

- **`player` est obligatoire.** Une carte s'affiche pour *un* joueur, et une borne en a plusieurs. Sans lui, l'événement ne sait pas où aller.
- **Le nom doit être celui du contenu qu'il désigne**, pas celui de votre source. Si votre table dit `Torch` et que la carte du jeu affiche `FIRE WATER`, écrivez `Fire Water` - sinon l'événement pointe vers une fiche qui n'existe pas.

## Décrire un score : une entrée, ou des morceaux qui ne se recouvrent pas

Un score est souvent éparpillé en mémoire - un chiffre par octet, une paire BCD, une moitié haute et une basse. L'agrégateur les **additionne** donc pour reconstituer le nombre, chacun pesé par ce que dit sa description ou son `score_mask`.

Mais **rien dans un `.MEM` ne dit « cette entrée EST le score entier »** plutôt qu'un morceau. Il faut donc choisir l'une des deux écritures, jamais les deux :

- **une entrée qui couvre tout** - un type large la lit d'un coup (`u24be`, `u32be`), et `score_mask` / `score_encoding` disent comment l'interpréter ;
- **plusieurs entrées, une par morceau**, dont les plages d'octets sont **disjointes**, chacune portant son poids.

Les mélanger casse le score, et ça arrive tout seul : on ajoute l'entrée complète sans retirer les morceaux qu'elle remplace. Sonic 1 affichait ainsi **110 pour un score de 100** - une `u24be` en `0xFE26` (qui couvre `FE26` à `FE28`) posée à côté des deux moitiés de la note RA, en `0xFE26` et `0xFE28`.

Le runtime se défend : quand deux morceaux couvrent un octet commun, **seul le plus large est gardé**, et l'autre est ignoré avec un avertissement qui les nomme tous les deux. Le fichier reste faux pour autant - deux descriptions du même nombre égareront le prochain lecteur.

## Traduire les valeurs : `map`

```lua
powerup_state = {
  { address=0x0756, type="u8", condition="change", desc="Player powerup state",
    map={ [0]="small", [1]="big", [2]="fire" } }
}
```

Le `map` transforme un nombre brut en mot stable - c'est ce que les effets lumineux exploitent (« fire » → panel rouge).

## Piloter les effets : `action` et `action_map`

Le runtime traduit automatiquement vos familles en commandes universelles : une baisse de `lives` émet `ACTION: DEAD`, une hausse de `scoring` émet `ACTION: SCORE`, un `flow` vers le titre émet `STATE: TITLE_SCREEN`… Pour forcer un comportement précis, utilisez les verbes génériques officiels dans `action`/`action_map` : `INVINCIBILITY_START`/`STOP`, `SPEED_START`/`STOP`, `SHIELD_GAIN`/`LOST`, `RING_GAIN`/`LOSE`, `TREASURE`, `BOSS_DEFEATED`, `LAP_COMPLETE`, `TURBO_BOOST`, `CRASH`, `DOOR_OPENED`, `SECRET_REVEALED`, `NIGHT_TIME`… Ainsi les Speed Shoes de Sonic et l'étoile de Mario allument les mêmes effets sur toutes les bornes.

## Éviter le spam : `no_log` et `no_survey`

- `no_log=true` ou `no_survey=true` : le runtime **ignore l'entrée dès le chargement** - l'adresse n'est pas surveillée et ne coûte rien en jeu.
- Ces entrées restent volontairement dans les fichiers du Data Pack : pour réactiver une adresse, passez son flag à `false` (ou supprimez-le - l'absence vaut `false`), aucun outil n'est nécessaire.
- Un anti-spam automatique protège de toute façon le runtime : un événement non-score qui se déclenche en boucle est coupé définitivement pour la session.

## Les règles d'or des descriptions

En anglais, courtes, orientées gameplay, sans point final, sans adresse dans le texte :

- ✅ `Player lives`, `Collected rings`, `Invincibility active`
- ❌ `ram address for number of lives`, `0x075A - Lives`

## Checklist avant de partager

- [ ] Les trois blocs dans l'ordre `game` → `rom` → `events`
- [ ] `game.system` = nom du dossier RetroBat
- [ ] Familles canoniques uniquement (`flow.lifecycle`, `scoring.points`…), `desc` en dernier champ, sans `=` ni le mot « address »
- [ ] `condition` reconnue : `change`, `eq`, `neq`, `increase`, `decrease`, `bit_true`, `bit_false`
- [ ] `no_log=true` sur les valeurs qui changent à chaque frame (réactivable en le passant à `false`)
- [ ] Testé en jeu : les événements apparaissent sur `ws://127.0.0.1:12345/ws/ingame` (avec leur `family`, et `color` pour les deltas score arcade)

!!! question "Un doute ?"
    Le modèle complet commenté est `resources\ram\<système>\template.MEM` quand il existe, et les fichiers du Data Pack sont autant d'exemples conformes. Les fichiers `.MEM` sont couverts par la [DATA-LICENSE](licences.md) - vos créations personnelles restent les vôtres, le partage communautaire est bienvenu.
