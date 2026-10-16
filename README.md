# Sillon, plugin Jellyfin

Plugin Jellyfin 12.2 compagnon de l'application Sillon. Il relie Jellyfin à Lidarr pour que la liste d'envies de musique tenue depuis l'app puisse être traitée automatiquement.

Phase 2 (cette version) : le plugin reprend la liste d'envies d'albums de l'ancien service Go `sillon-gateway` (mêmes routes, même JSON), à l'URL de base `<jellyfin>/Sillon`. Les notifications (APNs) arrivent en phase 3 : les jetons d'appareils sont déjà enregistrés, l'envoi ne fait rien pour l'instant.

## Installation

1. `scripts/package.sh` produit `dist/Sillon_0.3.0.0.zip` (la dll et `meta.json`).
2. Dézipper dans un dossier `Sillon_0.3.0.0/` du répertoire `plugins/` de Jellyfin.
3. Redémarrer Jellyfin.

## Configuration

Tableau de bord, Extensions, Sillon.

- **Traitement de la liste d'envies** : `Manuel` ou `Lidarr`.
- **Proposer la liste d'envies aux applications Sillon** : activé par défaut. Désactivé, `GET /Sillon/Config` renvoie `features: []` et les applications ne voient pas la fonctionnalité (utile pendant une migration) ; les routes continuent de répondre.
- **Délai avant échec** : une envie sans résultat après ce nombre de jours (14 par défaut) passe en échec.
- **Lidarr** : URL (par défaut `http://lidarr:8686`) et clé API, puis « Tester la connexion ». En cas de succès, la version de Lidarr s'affiche, la connexion est enregistrée et les listes « Profil de qualité », « Profil de métadonnées » et « Dossier racine » se remplissent. Choisir, puis « Enregistrer ».
- **Dossiers** : « Dossier de musique vu par Lidarr » (`/data/media/music`) et « Le même dossier vu par Jellyfin » (`/data/music`). Quand Lidarr importe un album, le chemin est converti et Jellyfin est prévenu pour qu'il l'indexe.
- **Webhook** : URL à coller dans Lidarr (Paramètres, Connexions, Webhook), événements Grab, Download (import) et échec de l'import. Le secret est généré au premier chargement du plugin.
- **Liste d'envies** : les envies (toutes pour un administrateur) avec leur état (En attente, En cours, Ajouté, Non disponible, Retiré) ; en mode manuel, le bouton « Marquer comme ajouté » règle une envie.
- **Notifications (bientôt)** : champs désactivés, réservés à la phase 3.

## Liste d'envies

- **Mode Lidarr** : une envie ajoute l'artiste (non surveillé) et l'album à Lidarr, surveille l'album et lance la recherche. Les événements du webhook (Grab, Download ou AlbumImport, ImportFailure) font avancer l'envie ; un import partiel est accepté. Quand l'album est dans la bibliothèque Jellyfin (le plugin l'attend jusqu'à 5 minutes), il est ajouté aux favoris du demandeur. Une tâche planifiée « Rapprocher la liste d'envies avec Lidarr » (toutes les 5 minutes, lançable depuis le tableau de bord) rattrape les événements perdus, relance un ajout resté en plan après 10 minutes et fait échouer les envies qui dépassent le délai.
- **Mode manuel** : l'envie est enregistrée sans appel à Lidarr. Un administrateur la marque comme ajoutée depuis la section « Liste d'envies » de la page du plugin ; les mêmes étapes suivent (attente de l'album dans la bibliothèque, favori, notification). La recherche dans le catalogue répond 503 (« mode manuel ») : l'accès direct à MusicBrainz viendra plus tard. La tâche planifiée ne fait ni relance ni échec automatique dans ce mode.
- Les données sont dans `requests.json` et `devices.json`, dans le dossier de données du plugin.

## API

- `GET /Sillon/health` : anonyme, `{ "status": "ok" }`.
- `GET /Sillon/Config` : utilisateur authentifié, `{ "version", "features", "mode" }`. `features` contient `requests` en mode manuel, ou en mode Lidarr quand l'URL et la clé sont renseignées, sauf si « Proposer la liste d'envies aux applications Sillon » est désactivé (alors `[]`).
- Utilisateur authentifié (jeton Jellyfin) : `GET /Sillon/v1/me`, `GET /Sillon/v1/catalog/search?q=`, `GET /Sillon/v1/catalog/artist/{mbid}`, `GET|POST /Sillon/v1/requests`, `DELETE /Sillon/v1/requests/{id}`, `POST /Sillon/v1/devices`, `DELETE /Sillon/v1/devices/{token}`. Un administrateur voit et retire toutes les envies, un utilisateur les siennes.
- Administrateur uniquement : `POST /Sillon/v1/requests/{id}/available`, `POST /Sillon/Lidarr/Test`, `GET /Sillon/Lidarr/QualityProfiles`, `GET /Sillon/Lidarr/MetadataProfiles`, `GET /Sillon/Lidarr/RootFolders`.
- Anonyme, protégé par le secret : `POST /Sillon/webhooks/lidarr?secret=`.

## Développement

```
dotnet build -warnaserror
dotnet test
scripts/package.sh
```
