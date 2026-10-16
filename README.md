# Sillon, plugin Jellyfin

Plugin Jellyfin 12.2 compagnon de l'application Sillon. Il relie Jellyfin à Lidarr pour que les demandes de musique faites depuis l'app puissent être traitées automatiquement.

Phase 2 (cette version) : le plugin reprend le service de demandes d'albums de l'ancien service Go `sillon-gateway` (mêmes routes, même JSON), à l'URL de base `<jellyfin>/Sillon`. Les notifications (APNs) arrivent en phase 3 : les jetons d'appareils sont déjà enregistrés, l'envoi ne fait rien pour l'instant.

## Installation

1. `scripts/package.sh` produit `dist/Sillon_0.2.0.0.zip` (la dll et `meta.json`).
2. Dézipper dans un dossier `Sillon_0.2.0.0/` du répertoire `plugins/` de Jellyfin.
3. Redémarrer Jellyfin.

## Configuration

Tableau de bord, Extensions, Sillon.

- **Traitement des demandes** : `Manuel` ou `Lidarr`.
- **Délai avant échec** : une demande sans résultat après ce nombre de jours (14 par défaut) passe en échec.
- **Lidarr** : URL (par défaut `http://lidarr:8686`) et clé API, puis « Tester la connexion ». En cas de succès, la version de Lidarr s'affiche, la connexion est enregistrée et les listes « Profil de qualité », « Profil de métadonnées » et « Dossier racine » se remplissent. Choisir, puis « Enregistrer ».
- **Dossiers** : « Dossier de musique vu par Lidarr » (`/data/media/music`) et « Le même dossier vu par Jellyfin » (`/data/music`). Quand Lidarr importe un album, le chemin est converti et Jellyfin est prévenu pour qu'il l'indexe.
- **Webhook** : URL à coller dans Lidarr (Paramètres, Connexions, Webhook), événements Grab, Download (import) et échec de l'import. Le secret est généré au premier chargement du plugin.
- **Demandes** : liste des demandes (toutes pour un administrateur) ; en mode manuel, le bouton « Marquer disponible » règle une demande.
- **Notifications (bientôt)** : champs désactivés, réservés à la phase 3.

## Demandes

- **Mode Lidarr** : une demande ajoute l'artiste (non surveillé) et l'album à Lidarr, surveille l'album et lance la recherche. Les événements du webhook (Grab, Download ou AlbumImport, ImportFailure) font avancer la demande ; un import partiel est accepté. Quand l'album est dans la bibliothèque Jellyfin (le plugin l'attend jusqu'à 5 minutes), il est ajouté aux favoris du demandeur. Une tâche planifiée « Rapprocher les demandes avec Lidarr » (toutes les 5 minutes, lançable depuis le tableau de bord) rattrape les événements perdus, relance un ajout resté en plan après 10 minutes et fait échouer les demandes qui dépassent le délai.
- **Mode manuel** : la demande est enregistrée sans appel à Lidarr. Un administrateur la marque disponible depuis la section « Demandes » de la page du plugin ; les mêmes étapes suivent (attente de l'album dans la bibliothèque, favori, notification). La recherche dans le catalogue répond 503 (« mode manuel ») : l'accès direct à MusicBrainz viendra plus tard. La tâche planifiée ne fait ni relance ni échec automatique dans ce mode.
- Les données sont dans `requests.json` et `devices.json`, dans le dossier de données du plugin.

## API

- `GET /Sillon/health` : anonyme, `{ "status": "ok" }`.
- `GET /Sillon/Config` : utilisateur authentifié, `{ "version", "features", "mode" }`. `features` contient `requests` en mode manuel, ou en mode Lidarr quand l'URL et la clé sont renseignées.
- Utilisateur authentifié (jeton Jellyfin) : `GET /Sillon/v1/me`, `GET /Sillon/v1/catalog/search?q=`, `GET /Sillon/v1/catalog/artist/{mbid}`, `GET|POST /Sillon/v1/requests`, `DELETE /Sillon/v1/requests/{id}`, `POST /Sillon/v1/devices`, `DELETE /Sillon/v1/devices/{token}`. Un administrateur voit et annule toutes les demandes, un utilisateur les siennes.
- Administrateur uniquement : `POST /Sillon/v1/requests/{id}/available`, `POST /Sillon/Lidarr/Test`, `GET /Sillon/Lidarr/QualityProfiles`, `GET /Sillon/Lidarr/MetadataProfiles`, `GET /Sillon/Lidarr/RootFolders`.
- Anonyme, protégé par le secret : `POST /Sillon/webhooks/lidarr?secret=`.

## Développement

```
dotnet build -warnaserror
dotnet test
scripts/package.sh
```
