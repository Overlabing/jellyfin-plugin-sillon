# Sillon, plugin Jellyfin

Plugin Jellyfin 12.2 compagnon de l'application Sillon. Il relie Jellyfin à Lidarr pour que les demandes de musique faites depuis l'app puissent être traitées automatiquement.

Phase 1 (cette version) : squelette du plugin, page de configuration Lidarr et endpoint de découverte. Le traitement des demandes arrive en phase 2.

## Installation

1. `scripts/package.sh` produit `dist/Sillon_0.1.0.0.zip` (la dll et `meta.json`).
2. Dézipper dans un dossier `Sillon_0.1.0.0/` du répertoire `plugins/` de Jellyfin.
3. Redémarrer Jellyfin.

## Configuration

Tableau de bord, Extensions, Sillon.

- **Traitement des demandes** : `Manuel` (rien n'est envoyé) ou `Lidarr`.
- **Lidarr** : URL (par défaut `http://lidarr:8686`) et clé API, puis « Tester la connexion ». En cas de succès, la version de Lidarr s'affiche, la connexion est enregistrée et les listes « Profil de qualité », « Profil de métadonnées » et « Dossier racine » se remplissent. Choisir, puis « Enregistrer ».
- **Webhook** : URL à coller dans Lidarr (Paramètres, Connexions, Webhook). Le secret est généré au premier chargement du plugin.
- **Notifications push (bientôt)** : champs désactivés, réservés à une phase ultérieure.

## API

- `GET /Sillon/health` : anonyme, `{ "status": "ok" }`.
- `GET /Sillon/Config` : utilisateur authentifié, `{ "version", "features", "mode" }`. `features` est vide jusqu'à la phase 2.
- Administrateur uniquement : `POST /Sillon/Lidarr/Test`, `GET /Sillon/Lidarr/QualityProfiles`, `GET /Sillon/Lidarr/MetadataProfiles`, `GET /Sillon/Lidarr/RootFolders`.

L'endpoint `/Sillon/webhooks/lidarr` affiché dans la page n'existe pas encore (phase 2).

## Développement

```
dotnet build -warnaserror
dotnet test
scripts/package.sh
```
