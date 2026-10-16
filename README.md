# Sillon, plugin Jellyfin

Plugin Jellyfin 12.2 compagnon de l'application Sillon. Il relie Jellyfin à Lidarr pour que la liste d'envies de musique tenue depuis l'app puisse être traitée automatiquement.

Le plugin reprend la liste d'envies d'albums de l'ancien service Go `sillon-gateway` (mêmes routes, même JSON), à l'URL de base `<jellyfin>/Sillon`. Phase 3 (cette version) : les notifications APNs partent directement du serveur Jellyfin vers les appareils du demandeur.

## Installation

1. `scripts/package.sh` produit `dist/Sillon_0.6.0.0.zip` (la dll et `meta.json`).
2. Dézipper dans un dossier `Sillon_0.6.0.0/` du répertoire `plugins/` de Jellyfin.
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
- **Notifications** : case « Activer », identifiant de clé, identifiant d'équipe (`XXXXXXXXXX` par défaut), topic (`com.overglass.sillon` par défaut) et clé privée `.p8`. La clé n'est jamais renvoyée par la page : elle s'affiche `***` une fois enregistrée, et enregistrer avec `***` la conserve. Une ligne d'état indique d'où vient la clé (environnement du serveur, champ ci-dessous, ou aucune). Le bouton « Envoyer une notification de test » envoie « Sillon : notifications actives » aux appareils de l'administrateur connecté et affiche le résultat par appareil.

## Notifications APNs

Le plugin parle à APNs en direct (HTTP/2, jeton JWT ES256 signé avec la clé `.p8`) ; un appareil enregistré avec l'environnement `development` passe par le bac à sable d'Apple, les autres par la production. Un appareil que APNs ne connaît plus (410, `BadDeviceToken`, `Unregistered`) est supprimé.

La clé peut venir de l'environnement du processus Jellyfin, qui l'emporte sur les champs de la page. Avec Docker :

```
environment:
  SILLON_APNS_KEY: "-----BEGIN PRIVATE KEY-----\nMIGT...\n-----END PRIVATE KEY-----"   # contenu du .p8, les \n littéraux sont acceptés
  SILLON_APNS_KEY_ID: "ABC123DEFG"
  SILLON_APNS_TEAM_ID: "XXXXXXXXXX"           # facultatif
  SILLON_APNS_TOPIC: "com.overglass.sillon"   # facultatif
```

La case « Activer » reste nécessaire. Un serveur tiers n'a pas la clé Apple de l'application : la liste d'envies y fonctionne, sans notification (un relais est prévu pour ce cas). `GET /Sillon/v1/me` renvoie `pushEnabled: true` quand les notifications sont actives.

## Liste d'envies

- **Mode Lidarr** : une envie ajoute l'artiste (non surveillé) et l'album à Lidarr, surveille l'album et lance la recherche. Les événements du webhook (Grab, Download ou AlbumImport, ImportFailure) font avancer l'envie ; un import partiel est accepté. Quand l'album est dans la bibliothèque Jellyfin (le plugin l'attend jusqu'à 5 minutes), il est ajouté aux favoris du demandeur. Une tâche planifiée « Rapprocher la liste d'envies avec Lidarr » (toutes les 5 minutes, lançable depuis le tableau de bord) rattrape les événements perdus, relance un ajout resté en plan après 10 minutes et fait échouer les envies qui dépassent le délai.
- **Mode manuel** : l'envie est enregistrée sans appel à Lidarr. Un administrateur la marque comme ajoutée depuis la section « Liste d'envies » de la page du plugin ; les mêmes étapes suivent (attente de l'album dans la bibliothèque, favori, notification). La recherche dans le catalogue répond 503 (« mode manuel ») : l'accès direct à MusicBrainz viendra plus tard. La tâche planifiée ne fait ni relance ni échec automatique dans ce mode.
- **Liste d'albums autorisés** (« Albums autorisés dans le catalogue ») : un MBID de release-group par ligne, lignes vides et commentaires `#` ignorés, casse indifférente. Vide (par défaut), tout le catalogue est proposé. Renseignée, la recherche et les pages d'artiste du catalogue ne montrent que ces albums (et les artistes qui en ont au moins un), et une envie pour un autre album est refusée en 400 (« Cet album n'est pas proposé sur ce serveur. »). La bibliothèque Jellyfin n'est pas touchée. Prévu pour un serveur de démonstration ne contenant que des albums Creative Commons (revue App Store).
- Les données sont dans `requests.json` et `devices.json`, dans le dossier de données du plugin.

## Migration depuis sillon-gateway

Reprend les envies et les appareils de l'ancien service Go dans le plugin (`scripts/migrate-from-gateway.py`, Python 3 sans dépendance).

1. Arrêter `sillon-gateway` et Jellyfin.
2. Copier la base SQLite du gateway (avec ses fichiers `-wal` et `-shm` s'ils existent) vers un dossier de travail ; ne jamais travailler sur l'original.
3. Contrôler sans rien écrire : `python3 -I scripts/migrate-from-gateway.py --db gateway.db --out /tmp/sillon --dry-run`. Le résumé donne le nombre d'envies par état et d'appareils par utilisateur ; les jetons ne sont jamais affichés.
4. Écrire les fichiers : même commande sans `--dry-run`. Le script refuse d'écraser un `requests.json` ou un `devices.json` non vides sans `--force`. Il convertit les identifiants d'utilisateur en 32 caractères hexadécimaux sans tirets (format `N` de Jellyfin) et toutes les dates en UTC. Les numéros d'envie sont conservés, le plugin continue après le plus grand.
5. Placer `requests.json` et `devices.json` dans le dossier de données du plugin (`DataFolderPath`). Jellyfin choisit `<plugins>/Jellyfin.Plugin.Sillon` s'il existe, sinon `<plugins>/Jellyfin.Plugin.Sillon_<version>` (qui change à chaque montée de version). Créer le premier, il survit aux mises à jour. Pour une installation Docker avec `/config` : `/config/plugins/Jellyfin.Plugin.Sillon/` (et non `Sillon_0.6.0.0/`, qui contient la dll). Les fichiers doivent appartenir à l'utilisateur de Jellyfin.
6. Démarrer Jellyfin, vérifier la section « Liste d'envies » du tableau de bord, puis activer « Proposer la liste d'envies aux applications Sillon ».

Le schéma actuel du gateway n'a pas de colonne `library_added_at` : le script la lit quand elle existe, sinon les envies disponibles sont reprises sans date d'ajout aux favoris.

## API

- `GET /Sillon/health` : anonyme, `{ "status": "ok" }`.
- `GET /Sillon/Config` : utilisateur authentifié, `{ "version", "features", "mode" }`. `features` contient `requests` en mode manuel, ou en mode Lidarr quand l'URL et la clé sont renseignées, sauf si « Proposer la liste d'envies aux applications Sillon » est désactivé (alors `[]`).
- Utilisateur authentifié (jeton Jellyfin) : `GET /Sillon/v1/me`, `GET /Sillon/v1/catalog/search?q=`, `GET /Sillon/v1/catalog/artist/{mbid}`, `GET|POST /Sillon/v1/requests`, `DELETE /Sillon/v1/requests/{id}`, `POST /Sillon/v1/requests/{id}/retry` (relance la recherche Lidarr d'une envie en attente, en cours ou échouée), `POST /Sillon/v1/devices`, `DELETE /Sillon/v1/devices/{token}`. Un administrateur voit et retire toutes les envies, un utilisateur les siennes.
- Administrateur uniquement : `POST /Sillon/v1/requests/{id}/available`, `POST /Sillon/Lidarr/Test`, `GET /Sillon/Lidarr/QualityProfiles`, `GET /Sillon/Lidarr/MetadataProfiles`, `GET /Sillon/Lidarr/RootFolders`, `GET /Sillon/Push/Status`, `POST /Sillon/Push/Test` (notification de test aux appareils de l'administrateur).
- Anonyme, protégé par le secret : `POST /Sillon/webhooks/lidarr?secret=`.

## Développement

```
dotnet build -warnaserror
dotnet test
python3 -I scripts/test_migrate.py
scripts/package.sh
```
