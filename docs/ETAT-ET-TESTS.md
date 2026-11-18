# État réel du projet (honnête)
## Vérifié dans l'environnement de génération (Linux, sans Windows)
- Chaîne de filtres ffmpeg : sources 640×360 et 1920×1080 -> 120 images BGR24 1280×720 en 4 s à 30 FPS (taille de flux exacte). OK.
- Syntaxe de `virtualcam.js` (node --check). OK.
## NON compilé / NON testé (aucun Windows, aucun accès réseau ici)
- Compilation C#/XAML, WebView2, NAudio, pilote softcam, VB-Cable, NSIS, GitHub Actions.
- Aucune mesure de RAM/CPU/FPS sur i7-4600U ; 1080p30 est probablement hors de portée de ce CPU en direct.
- Décodage matériel : `-hwaccel auto` demandé ; l'état réel n'est PAS détecté (affiché « demandé »).
## À faire sur votre PC (checklist)
1. `build.ps1` -> corriger d'éventuelles erreurs de compilation. 2. Installer. 3. camtest.html : résolution/FPS réels.
4. Appel Meet/Zoom 10 min : stabilité, images perdues (panneau Diagnostic). 5. Micro : choisir « CABLE Output ».
## Limites connues
Barre de titre Windows standard (onglets non intégrés à la barre de titre) ; pas de gestion de profils multiples ni de synchronisation ;
latence audio ≈ 80 ms à calibrer ; la qualité reçue dépend de la compression de l'application cible.
