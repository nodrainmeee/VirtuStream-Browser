# VirtuStream Browser

Navigateur Windows 10 (WPF + WebView2) avec lecteur vidéo local, caméra virtuelle DirectShow et micro virtuel.
Conçu pour : i7-4600U, 4 Go de RAM, Intel HD Graphics (Haswell).

## Architecture et choix
| Option | RAM | Caméra virtuelle Win10 | Verdict |
|---|---|---|---|
| Electron | +150–300 Mo (Chromium embarqué) | via module natif | trop lourd pour 4 Go |
| Tauri | léger | via module natif | écosystème WebRTC/Win10 moins mûr |
| **C# .NET Framework 4.8 + WebView2** | runtime Edge partagé ; .NET 4.8 déjà présent dans Windows 10 | P/Invoke vers filtre DirectShow | **retenu** |
| C++ / Qt | léger | natif | coût de développement et de maintenance élevé |

Pipeline : `fichier -> ffmpeg (HW auto, Lanczos, sans agrandissement) -> BGR24 -> softcam.dll -> périphérique caméra`.
Audio : `ffmpeg -> PCM 48 kHz via pipe nommé -> NAudio -> "CABLE Input"` ; les applications choisissent « CABLE Output ».
L'UI du panneau est native WPF : aucun pont JS<->hôte n'est exposé aux pages web (IPC web désactivé).

Pourquoi pas `MFCreateVirtualCamera` : API Windows 11 uniquement ; sous Windows 10 il faut un filtre DirectShow (softcam, MIT).
Chromium (Meet, Zoom Web, WhatsApp Web, Messenger) énumère les caméras DirectShow. Le nom visible est « DirectShow Softcam » tant que
vous ne recompilez pas softcam avec un nom personnalisé (constante dans son source) — c'est une source clairement virtuelle.

## Compiler / installer
`build.ps1` (ou GitHub Actions : `.github/workflows/build.yml`). L'installateur demande les droits administrateur
(enregistrement `regsvr32` du filtre caméra ; installation optionnelle de VB-Cable). Désinstallation : Paramètres > Applications,
ce qui désenregistre le filtre.

## Utilisation
1. Ouvrir le panneau (icône vidéo), choisir une vidéo. 2. Cocher « Caméra virtuelle Windows ». 3. Dans l'application cible,
choisir la caméra Softcam. Vérifiez avec `tools/camtest.html` (résolution et FPS réels reçus).
Une vidéo plus petite que la sortie est centrée à sa taille native (jamais agrandie artificiellement).
Si l'image est à l'envers, mettre `FlipVertical = true` dans `MediaEngine` (dépend de la version de softcam).

## Divulgation
Une caméra virtuelle sert à de la présentation, de la démo, du test ou de la confidentialité. Ne l'utilisez pas pour tromper
des interlocuteurs ou contourner une vérification d'identité ; beaucoup de services l'interdisent.
