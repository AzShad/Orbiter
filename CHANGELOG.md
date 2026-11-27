# Journal des versions

Toutes les versions d'Orbiter. Format : [SemVer](https://semver.org/lang/fr/) — `MAJEUR.MINEUR.CORRECTIF`.

## [1.2.0] - 2026-11-27
- L'application s'appelle désormais **Orbiter** (anciennement NovaTerm)
- Reprise automatique des sessions, empreintes et réglages de NovaTerm au premier lancement
- Système de versions : fichier `VERSION`, journal des versions, script `scripts/release.ps1`
- Publication automatique d'une Release GitHub (installeur + version portable) à chaque version

## [1.1.0] - 2026-11-27
- Thème sombre et thème clair, bascule en un clic, suit le thème de Windows par défaut
- Nouvelles boîtes de dialogue modernes (empreinte serveur, confirmations, erreurs, saisie)
- Fenêtre de création de session repensée
- Correction : plantage possible de la fenêtre de session à l'apparition de la barre de défilement

## [1.0.0] - 2026-11-27
- Terminal SSH en onglets (xterm-256color)
- Explorateur SFTP : glisser-déposer, transferts avec progression et annulation, édition à distance
- Tunnels SSH local, distant et SOCKS
- Authentification par mot de passe et clés (OpenSSH, PEM, PuTTY), secrets chiffrés DPAPI
- Vérification des empreintes serveur, connexion rapide
- Installeur Windows et version portable
