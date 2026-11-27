# Orbiter — client SSH / SFTP pour Windows

## Télécharger
Les versions sont publiées dans l'onglet **Releases** du dépôt :
- `Orbiter-Setup-X.Y.Z.exe` : installeur (menu Démarrer, icône bureau, désinstallation). Pas besoin des droits admin.
- `Orbiter-Portable-X.Y.Z.exe` : version portable, un seul fichier, rien à installer.

Prérequis : Windows 10 1809+ / 11 x64, avec Microsoft Edge WebView2 Runtime (déjà présent sur Windows 11).

## Fonctionnalités
- Terminal SSH en onglets (xterm-256color : couleurs, vim, htop, redimensionnement)
  - sélection = copier, clic droit = coller, Ctrl+Maj+C / Ctrl+Maj+V, Ctrl+molette = taille du texte
  - **Ctrl+F** : rechercher dans le terminal (Entrée / Maj+Entrée pour naviguer) ; **Ctrl+clic** sur un lien pour l'ouvrir dans le navigateur
  - **Commandes enregistrées** (bouton ▶) : envoyer une commande au terminal actif ou à tous les terminaux ; à personnaliser dans les Paramètres (roue dentée)
  - session terminée → touche **R** pour se reconnecter
- Explorateur SFTP ouvert automatiquement à côté du terminal (ou onglet SFTP seul)
  - envoi par glisser-déposer depuis l'Explorateur Windows, fichiers et dossiers (récursif)
  - téléchargement, renommer (F2), supprimer (Suppr), nouveau dossier, chmod, copier le chemin
  - double-clic sur un fichier = ouverture avec l'appli Windows associée, avec proposition de renvoi après modification
  - bouton « terminal » : fait un `cd` vers le dossier courant dans le terminal
  - progression, débit et annulation des transferts
- Tunnels SSH : local (-L), distant (-R), SOCKS dynamique (-D)
- Authentification : mot de passe / keyboard-interactive, clés privées OpenSSH, PEM et PuTTY .ppk (avec passphrase)
- Mots de passe et passphrases mémorisés chiffrés avec Windows DPAPI (liés à votre compte Windows)
- Vérification des empreintes serveur (alerte si l'empreinte change)
- Thème clair / sombre (bouton lune/soleil, suit Windows par défaut), boîtes de dialogue modernes
- Dossiers de sessions : glisser-déposer une session sur un dossier, clic sur un dossier pour le replier
- Connexion rapide `utilisateur@hôte:port` (essaie aussi les clés `~/.ssh/id_*` non chiffrées)

Données : `%APPDATA%\Orbiter` (sessions.json, known_hosts.json, settings.json).
Les données de l'ancienne version NovaTerm sont reprises automatiquement au premier lancement.

## Versions
- La version courante est dans le fichier [`VERSION`](VERSION) ; l'historique dans [`CHANGELOG.md`](CHANGELOG.md).
- Publier une nouvelle version :

```powershell
.\scripts\release.ps1 -Bump patch -Notes "Description de la modification" -Push
```

`patch` = correction, `minor` = nouvelle fonctionnalité, `major` = changement incompatible.
Le script met à jour `VERSION` et `CHANGELOG.md`, crée le commit et le tag `vX.Y.Z`, puis pousse :
GitHub Actions compile alors l'installeur et publie la Release automatiquement.

## Compiler en local
Prérequis : SDK .NET 10, Inno Setup 6.

```powershell
.\build.ps1
```

Les fichiers sont générés dans `dist\`.

## Outils de test
- `SmokeTest\` : test automatisé de la couche SSH/SFTP (à lancer contre un serveur de test).
- `UiPreview\` : enregistre une capture de chaque fenêtre en thème clair et sombre.

## Composants
SSH.NET (MIT), xterm.js (MIT), Microsoft WebView2.
