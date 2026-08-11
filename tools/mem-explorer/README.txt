MEM Explorer (bundled tool)
===========================

EN
--
MemExplorer.exe is the desktop RAM-discovery companion for APIExpose: it launches
a game through APIExpose, observes live RAM (the RetroArch discovery wrapper over a
named pipe, or the MAME Lua plugin over TCP) and helps author and test .MEM
definitions.

The binary is NOT tracked by Git (only this README is). It is built from its own
repository and shipped with APIExpose:

- Build / deploy locally:  plugins/MemExplorer/tools/release.ps1
  Publishes a framework-dependent single-file exe (needs the .NET Desktop Runtime,
  already present on the fleet) and copies it here as MemExplorer.exe.
- Distribution: MemExplorer.exe is embedded in the APIExpose installer, so end
  users get it without cloning the tool repository.

FR
--
MemExplorer.exe est l'outil bureau de decouverte RAM d'APIExpose : il lance un jeu
via APIExpose, observe la RAM en direct (wrapper de decouverte RetroArch sur pipe
nomme, ou plugin Lua MAME sur TCP) et aide a creer et tester les definitions .MEM.

Le binaire n'est PAS suivi par Git (seul ce README l'est). Il est construit depuis
son propre depot et distribue avec APIExpose :

- Build / deploiement local : plugins/MemExplorer/tools/release.ps1
  Publie un exe single-file framework-dependent (necessite le .NET Desktop Runtime,
  deja present sur le parc) et le copie ici sous MemExplorer.exe.
- Distribution : MemExplorer.exe est embarque dans l'installeur APIExpose, pour que
  les utilisateurs finaux l'obtiennent sans cloner le depot de l'outil.
