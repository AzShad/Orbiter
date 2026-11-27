# Journal des versions

Toutes les versions d'Orbiter. Format : [SemVer](https://semver.org/lang/fr/) — `MAJEUR.MINEUR.CORRECTIF`.

## [1.5.0] - 2026-11-28
- Recherche dans le terminal avec Ctrl+F : résultats surlignés, Entrée / Maj+Entrée pour passer de l'un à l'autre, option respecter la casse (Ctrl+F reste disponible dans vim, nano et htop ; Ctrl+Maj+F ouvre toujours la recherche)
- Liens cliquables dans le terminal : Ctrl+clic ouvre l'adresse dans le navigateur

## [1.4.1] - 2026-11-28
- Orbiter fonctionne désormais avec .NET 10, qui reçoit les correctifs de sécurité jusqu'en 2028 (.NET 8 n'est plus maintenu)
- Fichiers légèrement plus petits

## [1.4.0] - 2026-11-28
- Sauvegarde chiffrée des sessions (bouton disquette) : sessions, mots de passe mémorisés, clés privées et empreintes serveur dans un seul fichier .orbiter protégé par un mot de passe
- Restauration sur n'importe quel PC, même après une réinstallation de Windows : les sessions existantes sont conservées

## [1.3.0] - 2026-11-27
- Orbiter vérifie au démarrage si une nouvelle version est disponible et propose de l'installer
- Mise à jour en un clic : installation automatique puis redémarrage (version installée comme version portable)
- Bouton « Rechercher les mises à jour » dans la fenêtre À propos
- Possibilité d'ignorer une version proposée

## [1.2.1] - 2026-11-27
- Documentation complète de reprise du projet (CLAUDE.md) : architecture, installation d'un nouveau poste, tests, pièges connus
- Correction du script de publication qui produisait un numéro de version à 4 chiffres

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
