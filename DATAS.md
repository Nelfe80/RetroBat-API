# Panel Data Target

`panel_curator_ultimate.py` doit produire un fichier source unique par jeu. Ce fichier doit rester exploitable par plusieurs consommateurs: MAME, FBNeo, RetroArch/libretro et, plus tard, les templates systemes.

Le fichier source n'est pas un export final specifique a un emulateur. Il decrit les controles natifs du jeu, les choix de curation, et le mapping vers le panel RetroBat standard. Les exports MAME, FBNeo ou RetroArch doivent etre generes depuis cette source commune.

## Sources Autorisees

La structure cible s'appuie sur les donnees deja utilisees par `panel_curator5.py`:

- `resources/controls/controls.ini`
- `resources/colors/colors.ini`
- la logique de normalisation, d'heritage joueur, de patterns et de mapping de `panel_curator5.py`

Elle peut aussi integrer les sources complementaires MAME:

- `resources/outputs/mame/*.json` pour les inputs, outputs, mappings et informations d'origine MAME;
- `resources/outputs/outputs_type_index.txt` pour typer les outputs avec `value_type`.

Pour les templates systemes, `resources/panels/systems` est une source de donnees:

- `resources/panels/systems/*.xml` pour les layouts systeme statiques;
- `resources/panels/systems/*.lip` pour les evenements et macros dynamiques de panel.

Les fichiers `.lip` peuvent aussi exister pour des jeux arcade, par exemple dans `resources/panels/mame`. Ils sont alors integres comme donnees d'evenements du jeu.

Les anciens fichiers panel XML ou JSON generes ne sont pas des sources de verite. Ils peuvent servir d'exemples de sortie ou de migration, mais pas d'alimentation principale.

## Principes

Le format doit etre lossless pour les donnees utiles:

- conserver les champs natifs bruts et normalises;
- ne pas remplacer une information brute par une version derivee;
- separer les controles joueur, les entrees systeme, les axes et les sorties physiques;
- ne pas stocker les coordonnees graphiques `x` / `y` dans le fichier jeu;
- deduire les valeurs RetroBat, retropad, FBNeo et libretro depuis le `panel_slot`;
- consolider les donnees MAME dans un meme objet `mame` quand elles concernent le meme input.

## Racine

Structure generale:

```json
{
  "schema": "api_expose.panel.v1",
  "system": "mame",
  "rom": "1943kai",
  "game_name": "1943 Kai: Midway Kaisen (Japan)",
  "meta": {},
  "panel": {},
  "players": {},
  "system_template": {},
  "events": {},
  "mame": {},
  "context": []
}
```

Champs racine:

- `schema`: version du format.
- `system`: famille principale du jeu, par exemple `mame`, `fbneo`, `retroarch`.
- `rom`: identifiant ROM ou jeu.
- `game_name`: nom lisible.
- `meta`: metadonnees issues de `controls.ini`.
- `panel`: convention physique RetroBat.
- `players`: controles par joueur.
- `system_template`: layouts statiques issus de `resources/panels/systems/*.xml`, quand `scope` vaut `system`.
- `events`: evenements, macros, lampes, groupes et sequences issus des fichiers `.lip`.
- `mame`: donnees MAME globales, notamment `source_file`, `outputs`, `mappings`, `stats` et `game_context` si `resources/outputs/mame` est disponible.
- `context`: tags globaux facultatifs, par exemple `driving`, `gun`.

`scope` peut etre ajoute pour distinguer le niveau du fichier:

- `game`: fichier source d'un jeu arcade ou d'un jeu precise;
- `system`: template systeme reutilisable.

## Meta

`meta` conserve les champs issus de `controls.ini`:

```json
{
  "players": 2,
  "alternating": 0,
  "mirrored": 1,
  "tilt": 0,
  "cocktail": 1,
  "uses_service": 0,
  "misc_details": "A - Fire, B - Bomb"
}
```

Ces champs sont conserves tels quels. Les valeurs numeriques `0` / `1` restent compatibles avec la logique actuelle.

## Panel

`panel` decrit la convention physique commune. Les slots ne contiennent pas de coordonnees graphiques.

```json
{
  "convention": "retrobat_standard",
  "slots": {
    "1": {
      "retrobat_button": "A",
      "retropad_id": 8,
      "libretro_button": "a",
      "fbneo_button": "a"
    },
    "2": {
      "retrobat_button": "B",
      "retropad_id": 0,
      "libretro_button": "b",
      "fbneo_button": "b"
    },
    "3": {
      "retrobat_button": "X",
      "retropad_id": 9,
      "libretro_button": "x",
      "fbneo_button": "x"
    },
    "4": {
      "retrobat_button": "Y",
      "retropad_id": 1,
      "libretro_button": "y",
      "fbneo_button": "y"
    },
    "5": {
      "retrobat_button": "L1",
      "retropad_id": 10,
      "libretro_button": "leftshoulder",
      "fbneo_button": "leftshoulder"
    },
    "6": {
      "retrobat_button": "R1",
      "retropad_id": 11,
      "libretro_button": "rightshoulder",
      "fbneo_button": "rightshoulder"
    },
    "7": {
      "retrobat_button": "L2",
      "retropad_id": 12,
      "libretro_button": "lefttrigger",
      "fbneo_button": "lefttrigger"
    },
    "8": {
      "retrobat_button": "R2",
      "retropad_id": 13,
      "libretro_button": "righttrigger",
      "fbneo_button": "righttrigger"
    }
  },
  "system_slots": {
    "start": {
      "retrobat_button": "START",
      "retropad_id": 3,
      "libretro_button": "start",
      "fbneo_button": "start"
    },
    "coin": {
      "retrobat_button": "SELECT",
      "retropad_id": 2,
      "libretro_button": "select",
      "fbneo_button": "back"
    }
  }
}
```

Les layouts de joueurs referencent uniquement `panel_slot`. Les champs `retrobat_button`, `retropad_id`, `libretro_button` et `fbneo_button` sont derives depuis `panel.slots`.

## Players

Chaque joueur conserve ses devices, entrees systeme, boutons, axes, controles speciaux et layouts.

```json
{
  "1": {
    "devices": [],
    "system_inputs": {},
    "buttons": {},
    "axes": {},
    "mame_inputs_extra": [],
    "layouts": {}
  }
}
```

## Devices

Un joueur peut avoir plusieurs devices. Le premier device n'est pas obligatoirement un joystick.

```json
{
  "id": "D1",
  "label": "Analog Gun",
  "type": "gun",
  "raw": "Analog Gun+lightgun+P1_BUTTON1",
  "codes": ["lightgun", "P1_BUTTON1"],
  "color": "Red",
  "inputs": {},
  "axes": {}
}
```

Types de devices cibles:

- `joy2wayhorizontal`
- `joy2wayvertical`
- `joy4way`
- `joy8way`
- `double_joystick`
- `double_joystick_4way`
- `double_joystick_8way`
- `double_joystick_2way_vertical`
- `triggerstick`
- `top_fire_joystick`
- `rotary_joystick`
- `mechanical_rotary_joystick`
- `optical_rotary_joystick`
- `trackball`
- `spinner`
- `dial`
- `vertical_dial`
- `paddle`
- `vertical_paddle`
- `wheel`
- `pedal`
- `pedal2`
- `pedal3`
- `analog_stick`
- `yoke`
- `throttle`
- `shifter`
- `turntable`
- `roller`
- `handlebar`
- `gun`
- `lightgun`
- `mahjong_panel`
- `hanafuda_panel`
- `keypad`
- `gambling_panel`
- `poker_panel`
- `slot_panel`
- `only_buttons`
- `misc`
- `unknown`

`raw` et `codes` conservent la forme issue de `controls.ini`.

## Device Inputs

Les inputs digitaux directionnels restent dans `inputs`.

```json
{
  "up": {
    "function": "Up",
    "mame": {
      "type": "P1_UP",
      "normalized_type": "P1_UP",
      "tag_raw": ":P1",
      "tag": "P1",
      "mask_dec": 8,
      "mask_hex": "0x0008",
      "defvalue_dec": 8,
      "defvalue_hex": "0x0008",
      "input_id": "P1_JOYSTICK_UP",
      "input": "JOYSTICK_UP",
      "input_label": "JOYSTICK_UP",
      "input_function": "joystick_up",
      "port_block": "005",
      "comment": "",
      "player": 1
    }
  }
}
```

Pour les doubles joysticks, les inputs doivent pouvoir distinguer les deux manches:

```json
{
  "sticks": {
    "left": {
      "up": {},
      "down": {},
      "left": {},
      "right": {}
    },
    "right": {
      "up": {},
      "down": {},
      "left": {},
      "right": {}
    }
  }
}
```

## Axes

Les controles analogiques utilisent `axes`.

```json
{
  "x": {
    "function": "Lightgun X",
    "mame": {
      "input_id": "P1_LIGHTGUN_X",
      "input": "LIGHTGUN_X",
      "input_label": "LIGHTGUN_X",
      "input_function": "lightgun_x",
      "port_block": "area51",
      "comment": "",
      "player": 1
    }
  },
  "y": {
    "function": "Lightgun Y",
    "mame": {
      "input_id": "P1_LIGHTGUN_Y",
      "input": "LIGHTGUN_Y",
      "input_label": "LIGHTGUN_Y",
      "input_function": "lightgun_y",
      "port_block": "area51",
      "comment": "",
      "player": 1
    }
  }
}
```

Axes cibles courants:

- `x`
- `y`
- `z`
- `dial`
- `dial_v`
- `paddle`
- `paddle_v`
- `trackball_x`
- `trackball_y`
- `lightgun_x`
- `lightgun_y`
- `pedal`
- `pedal2`
- `pedal3`
- `positional`
- `positional_v`
- `positional_h`
- `mouse_x`
- `mouse_y`

## Buttons

Les boutons joueur conservent le nom logique, la fonction, la couleur et les donnees MAME consolidees.

```json
{
  "1": {
    "logical_name": "A",
    "function": "Fire",
    "color": "Red",
    "mame": {
      "type": "P1_BUTTON_1",
      "normalized_type": "P1_BUTTON_1",
      "tag_raw": ":P1",
      "tag": "P1",
      "mask_dec": 16,
      "mask_hex": "0x0010",
      "defvalue_dec": 16,
      "defvalue_hex": "0x0010",
      "input_id": "P1_BUTTON1",
      "input": "BUTTON1",
      "input_label": "BUTTON1",
      "input_function": "button1",
      "port_block": "1943",
      "comment": "",
      "player": 1
    }
  }
}
```

`mame` consolide les donnees de port MAME et les donnees issues de `resources/outputs/mame.inputs[]`.

Les champs `type`, `normalized_type`, `tag_raw`, `tag`, `mask_dec`, `mask_hex`, `defvalue_dec` et `defvalue_hex` viennent de la logique de port MAME.

Les champs `input_id`, `input`, `input_label`, `input_function`, `port_block`, `comment` et `player` viennent de `resources/outputs/mame.inputs[]`.

`function` au niveau bouton reste la fonction humaine/curator. `mame.input_function` reste la fonction technique extraite de MAME.

## System Inputs

`system_inputs` conserve les entrees systeme principales et les entrees supplementaires.

```json
{
  "start": {
    "color": "White",
    "mame": {}
  },
  "coin": {
    "color": "White",
    "mame": {}
  },
  "extra": {
    "service1": {},
    "tilt": {},
    "volume_up": {},
    "volume_down": {},
    "test": {},
    "door": {},
    "bill1": {},
    "memory_reset": {}
  }
}
```

Les entrees systeme supplementaires restent disponibles pour l'API meme si elles ne sont pas mappees sur le panel standard.

## MAME Inputs Extra

`mame_inputs_extra` conserve les inputs MAME qui n'entrent pas directement dans `buttons`, `axes` ou `system_inputs`.

```json
[
  {
    "input_id": "P1_HANAFUDA_A",
    "input": "HANAFUDA_A",
    "input_label": "HANAFUDA_A",
    "input_function": "hanafuda_a",
    "port_block": "airduel",
    "comment": "",
    "player": 1
  }
]
```

Cette section permet de conserver les familles specialisees:

- mahjong;
- hanafuda;
- gambling;
- poker;
- slot;
- keypad;
- bill/door/service/test;
- inputs techniques ou non mappes.

## Layouts

Les layouts representent les decisions de curation.

```json
{
  "2-Button": {
    "pattern": "line_2",
    "buttons": {
      "1": {
        "panel_slot": 1
      },
      "2": {
        "panel_slot": 2
      }
    }
  },
  "4-Button": {
    "pattern": "line_2",
    "buttons": {
      "1": {
        "panel_slot": 1
      },
      "2": {
        "panel_slot": 2
      }
    }
  }
}
```

Un layout vide est une information valide:

```json
{
  "2-Button": {
    "pattern": "neo_4",
    "buttons": {}
  }
}
```

`panel_slot` est la seule information necessaire pour deduire:

- le bouton RetroBat;
- le `retropad_id`;
- le nom libretro;
- le nom FBNeo.

## Patterns

Patterns cibles:

- `line_1`
- `line_2`
- `line_3_bottom`
- `triangle_top1`
- `square_4`
- `neo_3`
- `neo_4`
- `capcom_6_straight`
- `mk5_block`
- `mk6`

Le pattern est une aide de placement. Le mapping final reste le contenu de `layouts.*.buttons.*.panel_slot`.

## System Templates

Les fichiers `resources/panels/systems/*.xml` sont une source pour les templates systemes. Ils decrivent des layouts par systeme, pas des jeux individuels.

Un fichier systeme cible utilise `scope: "system"`:

```json
{
  "schema": "api_expose.panel.v1",
  "scope": "system",
  "system": "nes",
  "panel": {},
  "system_template": {
    "layouts": {}
  },
  "events": {}
}
```

Chaque layout systeme conserve les attributs utiles du XML:

```json
{
  "type": "6-Button",
  "name": "Super Advantage",
  "panel_buttons": 6,
  "retropad_device": null,
  "retropad_analog_dpad_mode": null,
  "joystick": {
    "color": "White"
  },
  "buttons": {
    "1": {
      "panel_slot": 1,
      "physical": 1,
      "controller": "A",
      "retropad_id": 8,
      "game_button": "A",
      "function": "A",
      "color": "Yellow"
    },
    "START": {
      "physical": 7,
      "controller": "START",
      "retropad_id": 3,
      "game_button": "START",
      "function": "START",
      "color": "Gray"
    }
  }
}
```

Les coordonnees XML `x` et `y` ne sont pas conservees dans le fichier cible. Elles restent liees a la convention d'affichage.

Un meme systeme peut avoir plusieurs layouts avec le meme `type`. Le champ `name` doit donc etre conserve. L'identifiant interne d'un layout peut combiner `type` et `name`, par exemple `8-Button:Arcade-Shark 8B`.

Les `retropad_id` systemes ne sont pas limites aux valeurs RetroPad standard 0-13. Certains templates utilisent des ids specifiques comme 20, 21, 22 ou 23. Ces valeurs sont conservees telles quelles.

## LIP Events

Les fichiers `.lip` decrivent des comportements dynamiques de panel. Ils peuvent etre presents pour un systeme ou pour un jeu arcade.

La section cible commune est `events`:

```json
{
  "events": {
    "lamps": [],
    "groups": [],
    "events": [],
    "lifecycle": [],
    "sequences": []
  }
}
```

Les `.lip` systemes sont particulierement utiles pour les panels limites physiquement. Ils peuvent decrire des modes temporaires ou des modificateurs qui donnent acces a des boutons supplementaires de l'emulateur sans changer le layout statique de base.

Exemple: pour N64, un bouton physique peut servir de modificateur afin d'afficher ou d'activer temporairement les C-buttons. Le layout statique conserve le mapping principal; l'event decrit la reaction du panel pendant l'appui.

Ces events sont optionnels mais fonctionnels. Leur absence ne bloque pas la generation du fichier cible. Leur presence enrichit l'exploitation du panel par une UI, un moteur d'eclairage ou APIExpose.

Les `.lip` alimentent uniquement `events`. Ils ne doivent pas modifier `system_template.layouts`, `players.*.layouts`, `panel_slot`, `function` ou `color` de base.

### Lamps

Les mappings statiques de lampes associent un output de jeu a un bouton ou indicateur panel.

Source LIP:

```xml
<lamp output="FIRE_LAMP" type="button" panel="1" button="B1" pressAction="B1" color="ORANGE"/>
```

Cible:

```json
{
  "output": "FIRE_LAMP",
  "type": "button",
  "panel": "1",
  "button": "B1",
  "press_action": "B1",
  "color": "ORANGE"
}
```

### Groups

Les groupes rassemblent plusieurs boutons ou indicateurs pour les piloter ensemble.

```json
{
  "name": "Torpedoes",
  "panel": "1",
  "members": ["B3", "B4", "B5", "B8"],
  "mapping": {
    "output": "TORP_COUNT",
    "max": 4
  }
}
```

### Events

Les events reagissent a un input physique ou a un output de jeu.

Exemple systeme:

```json
{
  "layout_type": "8-Button",
  "layout_name": "Arcade-Shark 8B",
  "purpose": "physical_limit_hint",
  "type": "input",
  "button": "B8",
  "trigger": "press",
  "description": "C-buttons mode",
  "macros": [
    {
      "type": "set_panel_colors",
      "colors": [
        {"target": "CURRENT", "button": "B1", "color": "YELLOW"}
      ]
    }
  ]
}
```

Exemple arcade:

```json
{
  "output": "READY_LAMP",
  "trigger": "on",
  "macros": [
    {
      "type": "blink_panel",
      "color": "WHITE",
      "duration": 800,
      "interval": 200
    }
  ]
}
```

### Lifecycle

Le lifecycle decrit les macros declenchees par l'etat global.

```json
{
  "event": "gameSelected",
  "macros": [
    {
      "type": "breath",
      "color": "WHITE",
      "duration": 1500
    }
  ]
}
```

Evenements lifecycle observes:

- `gameSelected`
- `gameStart`
- `gameEnd`
- `idle`

### Sequences

Les sequences detectent une succession d'etats d'outputs dans une fenetre temporelle.

```json
{
  "name": "TripleTorpedo",
  "window": 1500,
  "steps": [
    {"output": "TORP_LAMP", "state": "on"},
    {"output": "TORP_LAMP", "state": "off"},
    {"output": "TORP_LAMP", "state": "on"}
  ],
  "macros": [
    {
      "type": "chase",
      "color": "RED",
      "direction": "right",
      "duration": 1000
    }
  ]
}
```

Les macros doivent conserver leur `type` et tous leurs parametres. Les valeurs de couleur, duree, intervalle, panel, bouton, groupe ou direction restent telles que decrites dans le `.lip`.

## MAME

Les donnees MAME d'un input doivent conserver les champs de port/tag/mask et les champs issus de `resources/outputs/mame.inputs[]`.

```json
{
  "type": "P1_BUTTON_1",
  "normalized_type": "P1_BUTTON_1",
  "tag_raw": ":P1",
  "tag": "P1",
  "mask_dec": 16,
  "mask_hex": "0x0010",
  "defvalue_dec": 16,
  "defvalue_hex": "0x0010",
  "input_id": "P1_BUTTON1",
  "input": "BUTTON1",
  "input_label": "BUTTON1",
  "input_function": "button1",
  "port_block": "005",
  "comment": "",
  "player": 1
}
```

`tag_raw` et `tag` ont des roles differents:

- `tag_raw`: valeur brute MAME, avec le prefixe eventuel `:`;
- `tag`: version normalisee pour l'affichage ou la comparaison.

`mask_dec` est conserve comme valeur de ciblage directe. `mask_hex` est conserve comme representation lisible et compatible avec les exports precedents.

`input_label` et `input_function` sont volontairement prefixes pour eviter toute confusion avec `function` au niveau bouton, axe ou entree systeme.

## MAME Global

Les donnees globales issues de `resources/outputs/mame` sont stockees dans l'objet racine `mame`.

```json
{
  "mame": {
    "source_file": "src\\mame\\sega\\segag80r.cpp",
    "inputs": [],
    "outputs": [],
    "mappings": [],
    "stats": {},
    "game_context": []
  }
}
```

`inputs` contient les inputs bruts du fichier `resources/outputs/mame`. Les inputs consolides sont aussi copies dans l'objet `mame` du bouton, de l'axe ou de l'entree systeme correspondant.

Un input brut conserve:

```json
{
  "input_id": "P1_BUTTON1",
  "input": "BUTTON1",
  "input_label": "BUTTON1",
  "input_function": "button1",
  "port_block": "005",
  "comment": "",
  "player": 1
}
```

Tous les outputs de `resources/outputs/mame/{rom}.json.outputs[]` sont integres dans `mame.outputs`, meme si leur role n'est pas connu ou pas directement lie au panel.

Un output conserve:

```json
{
  "name": "coin_counter1",
  "player": 1,
  "index": 1,
  "value_type": "integer"
}
```

`value_type` vient de `resources/outputs/outputs_type_index.txt`.

Valeurs attendues:

- `boolean`: sortie ON/OFF, par exemple lampe, LED, lockout, recoil ou moteur active/desactive;
- `integer`: compteur ou valeur numerique, par exemple `coin_counter` ou caractere d'afficheur;
- `unknown`: type non determine.

L'enrichissement par `value_type` ne remplace aucune donnee de `resources/outputs/mame`; il ajoute uniquement une information de typage.

Aucun output ne doit etre filtre a cause de son nom, de son type ou de son absence de mapping. Le fichier cible garde la liste complete.

Un mapping conserve:

```json
{
  "input_id": "COIN1",
  "input": "COIN1",
  "player": 1,
  "input_label": "COIN1",
  "input_function": "coin1",
  "output": "coin_counter1"
}
```

Tous les mappings de `resources/outputs/mame/{rom}.json.mappings[]` sont integres dans `mame.mappings`.

Les champs sont harmonises comme pour les inputs:

- `label` devient `input_label`;
- `function` devient `input_function`;
- `input_id`, `input`, `player` et `output` sont conserves.

Les mappings restent une table globale `input MAME -> output MAME`. Aucun champ de resolution comme `resolved_input_path` ou `resolved_output_path` n'est stocke dans le fichier cible. Ces chemins peuvent etre calcules par les outils consommateurs si necessaire.

Familles d'outputs a reconnaitre:

- `lamp`
- `led`
- `coin_counter`
- `coin_lockout`
- `start_lamp`
- `digit`
- `segment_display`
- `vfd`
- `display`
- `score_display`
- `credit_display`
- `time_display`
- `meter`
- `tachometer`
- `speed`
- `gear`
- `recoil`
- `motor`
- `vibration_motor`
- `wheel_motor`
- `knocker`
- `brake`
- `door_lamp`
- `credit_led`

Les familles d'affichage couvrent les sorties de scores, credits, compteurs visibles, chronometres et afficheurs de borne. Elles peuvent apparaitre sous des noms comme:

- `digit00`, `digit01`, `digit100`;
- `seg_digit_1`;
- `vfd0`, `vfd_1_char_1`;
- `display_pattern_0`, `display_step_0`;
- `credit`, `creditoutmeter`, `creditspendmeter`;
- `time0`, `time1`;
- `speed`, `tachometer`;
- `p1gear`, `p2gear`.

Ces familles servent a l'exploitation et a l'affichage, pas au filtrage. Les sorties restent toutes dans `mame.outputs`. Elles ne sont pas des inputs panel, mais elles sont utiles pour APIExpose, les afficheurs externes, les overlays ou les integrations physiques.

## Export Targets

Le fichier canonique est le point d'entree unique.

Les exports sont des transformations:

- MAME: utilise les objets `mame`, `panel.slots` et `layouts`.
- FBNeo: utilise `panel.slots.*.fbneo_button` et `system_slots`.
- RetroArch/libretro: utilise `retropad_id` et `libretro_button`.
- Systems: utilisera plus tard la meme convention panel et les mappings par systeme.

Les exports ne doivent pas reintroduire de logique metier divergente. Toute decision de curation doit rester dans le fichier source.
