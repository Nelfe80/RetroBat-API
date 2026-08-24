# Arcade et panels

APIExpose expose des **données arcade** pour bornes, LEDs et thèmes : les plugins comme [LedManager](https://nelfe80.github.io/RetroBat-Led-Manager/) s'en servent pour colorer vos boutons, et [MarqueeManager](https://nelfe80.github.io/RetroBat-Marquee-Manager/) pour animer lampes et scores.

## Les panels (dynpanels)

Le dossier `resources\dynpanels\` contient les définitions des panneaux de contrôle : boutons, couleurs, fonctions des commandes, layouts CPO - par système et par jeu. C'est grâce à elles que vos boutons prennent les couleurs des contrôles réels du jeu sélectionné.

## Le panneau dessiné, pour les thèmes

À chaque jeu sélectionné, APIExpose **dessine votre panneau de contrôle** et l'écrit en SVG, prêt à être affiché par un thème EmulationStation :

```
resources\theme\panels\<système>\<jeu>.svg        vue de dessus
resources\theme\panels\<système>\<jeu>-3d.svg     vue de face
resources\theme\panels\<système>\default.svg      repli du système
```

Les mêmes fichiers sont recopiés sous `\.emulationstation\themes\.panels\`, le seul endroit qu'un thème peut lire de façon fiable. Le dossier commence par un point pour ne jamais se mélanger aux thèmes installés.

Ce que montre le dessin :

- **les boutons que votre borne possède vraiment** - pas ceux du jeu. Vous voyez vos huit trous, et lesquels servent à ce jeu ;
- les boutons **utilisés par le jeu** portent leur couleur et leur fonction (`Fire`, `Loop`…), les autres restent dessinés en transparence ;
- le joystick est dessiné s'il y en a un, dans la couleur que la définition du jeu lui donne.

C'est du vecteur : un thème le met à l'échelle qu'il veut, il reste net sur un marquee 4K comme sur une vignette.

!!! note "Écriture atomique"
    Le fichier est écrit à côté puis déplacé en place. Un thème qui le lit pendant qu'un jeu démarre - l'instant précis où le panneau est réécrit - voit l'ancien dessin ou le nouveau, jamais la moitié d'un.

## Vérifier son câblage

Le flux `/ws/panel` publie les **appuis physiques de la borne**, déjà résolus en emplacements de panneau (`panel.input.pressed` / `panel.input.released`) : jamais un numéro de bouton brut, mais l'emplacement et sa fonction.

C'est ce qui rend la vérification de câblage possible **sans aucun matériel LED** : vous appuyez sur le bouton en bas à gauche, l'emplacement en bas à gauche s'allume. Si c'en est un autre, le câblage n'est pas celui que la borne déclare - et ça se voit en une seconde. [MarqueeManager](https://nelfe80.github.io/RetroBat-Marquee-Manager/) s'en sert pour son calque panneau.

START et SELECT sont annoncés comme **entrées système** plutôt que comme « aucun emplacement » : ils sont câblés sur leurs propres broches, hors des emplacements numérotés.

## Les définitions RAM

Le dossier `resources\ram\` contient les définitions mémoire des jeux (fichiers `.MEM`) : elles permettent de détecter en temps réel les événements d'une partie - score, vies, power-ups - directement dans la RAM du jeu. Vous pouvez écrire les vôtres : voir [Créer ses fichiers .MEM](mem.md).

!!! note "Le Data Pack"
    `dynpanels`, `ram`, gamelists et autres données de `resources\` constituent l'**APIExpose Data Pack**, fruit d'un long travail de curation. Il est inclus dans l'archive `full` des releases et protégé par sa propre licence (`DATA-LICENSE.md`) - voir [Licences](licences.md).

## Le wrapper RetroArch

Pour lire la RAM des jeux, APIExpose s'appuie sur `wrapper\wrapper.dll`, une DLL proxy libretro qui s'intercale entre RetroArch et le cœur d'émulation, sans modifier RetroArch.

Chaque version publiée est accompagnée de son empreinte dans `wrapper\WRAPPER_VERSION.txt` :

```powershell
Get-FileHash wrapper\wrapper.dll -Algorithm SHA256
```

Le hash obtenu doit correspondre à celui du fichier `WRAPPER_VERSION.txt` et des notes de release. S'il diffère, n'utilisez pas la DLL.

## High scores et sorties MAME

APIExpose expose aussi :

- les **high scores** des jeux arcade (via hi2txt) ;
- les **sorties natives MAME** (`READY_LAMP`, `TORP_LAMP_1`…) sur le flux `/ws/arcade`, pour que lampes et LEDs revivent comme sur la borne d'origine ;
- le contexte du jeu courant et les événements runtime, pour overlays, thèmes ou outils externes.

Le détail des flux temps réel est dans [API locale](api.md).
