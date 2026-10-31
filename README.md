# Sillon for Jellyfin

A Jellyfin plugin that powers the music catalogue, album requests and push notifications of the
[Sillon](https://apps.apple.com/app/id6822914803) iOS and macOS app. It is for server administrators:
install it, point it at Lidarr if you want requests fulfilled automatically, and the people who use
Sillon on your server can ask for albums and be told when they are ready.

Sillon itself is a music player for Jellyfin. This plugin adds the server side of its request features;
playback and browsing your existing library work without it. The app's privacy policy is at
<https://overlabing.github.io/sillon/privacy>.

## Features

- **Catalogue search.** People search MusicBrainz-backed artists and albums from the app. The search goes
  through your Lidarr, so it needs Lidarr to be set up (without it, the catalogue answers 503 and only
  the manual wishlist below is available).
  - **Search by song.** Typing a track title (`Creep`, `Radiohead - Creep`, `Creep by Radiohead`) also offers the
    albums that contain it, studio albums first, then compilations and live albums. The plugin asks
    MusicBrainz directly (one request a second, an identifying User-Agent, answers kept for a day) and
    resolves each album through Lidarr, so requests follow the usual path. Switch it off with *Find albums by song title*.
    Each album found this way carries an optional `matchedTrack` (`title`, `recordingMbid`, and `disc` and
    `position` when known).
  - **Tracklist of a catalogue album.** `GET /Sillon/v1/catalog/album/{mbid}/tracks[?recording=<mbid>][&track=<title>]`
    (`mbid` is the album's release group id) returns `{albumMbid, releaseMbid, title, discCount, tracks:
    [{disc, position, number, title, durationMs?, recordingMbid, matched?}]}`, where `matched` flags the track
    asked about. Errors: 400 malformed id, 404 unknown or not offered album, 502 MusicBrainz unavailable, 503
    manual mode. The tracks come from MusicBrainz, so it works for albums that are not on the server yet.
  - **Singles are hidden** by default, because they are rarely available from download sources while the album
    that holds the song is. *Hide singles* and *Hide EPs* are plugin settings; neither applies when the catalogue
    is limited to an allow list.
- **Wishlist and requests.** A user asks for an album; the plugin follows the request until the album is
  in your Jellyfin library, then adds it to that user's favourites and notifies them. Two fulfilment modes:
  - **Lidarr**: the plugin adds the album to Lidarr with its artist, starts the search, and
    follows Lidarr's webhook events (grab, import, import failure). A scheduled task (every 5 minutes)
    catches missed events, retries stalled adds, and fails requests that wait longer than the timeout
    (14 days by default); if the album is imported after that, the request becomes available and the user is
    told. Until a release is grabbed a request stays `requested`, and request records carry
    an optional `phase` (`searching`, then `notFoundYet` after 3 days by default) for apps that tell the two
    apart; older apps show it as waiting. Once a week it asks Lidarr to look for better quality ("cutoff unmet").
    The artist is monitored, because Lidarr's wanted list and RSS sync skip the albums of an unmonitored artist,
    but only the requested album is: the artist's other albums and future releases are left alone. Cancelling a
    request unmonitors its album.
  - **Manual**: requests are only recorded. An administrator marks each one as added once the album is in
    the library; the rest of the flow is the same.
- **Request limits.** Cap how many requests one person can have in progress, and how many they can make
  per day (0 means no limit). Administrators can be exempted.
- **Allow-list.** Optionally restrict what can be requested to a list of MusicBrainz release-group ids
  (useful for a demo server).
- **Notifications** when a requested album is ready, in one of three modes: through the Sillon relay,
  directly to Apple with your own key, or off. See [Notifications](#notifications).
- **Administration page** in the Jellyfin dashboard: overview, request list with covers, guided Lidarr
  setup, notification settings, limits and clean-up of old requests.

## Requirements

- Jellyfin **12.2** or newer (the plugin is built against Jellyfin 12.2.0, `targetAbi` 12.2.0.0).
- **Lidarr** is optional. Without it, requests are manual and the catalogue search is unavailable.
  With it, the plugin needs Lidarr's address and API key, and Lidarr must be able to reach Jellyfin to
  deliver its webhook.
- For notifications through the relay: nothing else. For direct notifications: an Apple developer account
  and an APNs key (see below).

## Installation

Add the Sillon plugin repository to Jellyfin, then install the plugin from the catalogue:

1. In the Jellyfin dashboard, open Plugins, then Repositories (Catalog, then the settings icon on older
   versions), and add a repository named `Sillon` with this URL:

   ```
   https://overlabing.github.io/jellyfin-plugin-sillon/manifest.json
   ```

2. Open the Catalog, find **Sillon** under Music, and install it.
3. Restart Jellyfin. The plugin appears under Dashboard, Plugins, with its own entry in the sidebar.
   New versions show up in the catalogue the same way.

To install by hand instead, download `Sillon_<version>.zip` from the
[latest release](https://github.com/Overlabing/jellyfin-plugin-sillon/releases/latest), unzip it (it holds
`Jellyfin.Plugin.Sillon.dll` and `meta.json`) into a folder named `Sillon_<version>` inside Jellyfin's
`plugins` directory (for example `/config/plugins/Sillon_0.8.0.0/` in the official Docker image), and
restart Jellyfin.

Requests and registered devices are stored in `requests.json` and `devices.json` in the plugin's data
folder, which survive upgrades.

## Configuration

Open the Sillon page in the Jellyfin dashboard. It has five tabs.

1. **Overview**: whether Lidarr is reachable, whether notifications are working, and how many requests
   and phones are known.
2. **Lidarr**: turn on "Add requested albums to Lidarr automatically", then follow the four steps: paste
   Lidarr's address (for example `http://lidarr:8686`), paste its API key (Lidarr, Settings, General),
   test the connection, and choose the quality profile, metadata profile and root folder. Under
   Advanced, copy the webhook link and add it in Lidarr (Settings, Connect, Webhook, method POST, events
   "On Grab", "On Release Import" and "On Import Failure"). The link contains a secret; treat it like a
   password. If Lidarr and Jellyfin see your music at different paths (separate containers), fill in both
   music folders so imported albums can be found.
3. **Requests**: every request with its state (waiting, downloading, available, not available,
   cancelled). Administrators can cancel any request, mark manual requests as added, and see all users'
   requests.
4. **Notifications**: choose a mode and see the phones registered for this server. A test button sends a
   notification to your own phones.
5. **Settings**: let people request albums (when off, the app hides the feature), request timeout, limits,
   optional allow-list of albums, and clean-up of finished requests.

## Notifications

A phone registers with your server when its owner opens the Sillon app signed in to it. Three modes:

- **Relay (default for new installs).** Nothing to set up. Your server builds the notification (which
  album, which artist, which state), encrypts it so that only the target phone can read it, signs it
  with a key unique to your server, and hands it to the relay, which forwards it to Apple's push service.
  - The relay **never sees the content**: it receives an opaque, fixed-size, encrypted envelope and the
    identifier of the target phone (a hash of the phone's public key). It cannot tell which album or user
    a notification is about.
  - What the relay necessarily handles: your server's public key (the identity it signs with), the
    identifiers of phones that declared your server, counters and sequence numbers for rate limiting and
    replay protection, and whatever its delivery to Apple requires. It also sees the IP address your
    server connects from and the timing of notifications, as any web service would, and it can block a
    server that abuses it. Its exact retention is the publisher's to state; see the privacy policy.
  - Your album titles, artists, user names and library never leave your server in readable form.
  - The relay is run by the publisher of the Sillon app. The App Store app only talks to the official
    relay; a custom relay address only works with an app build made for it.
- **Direct.** Your server talks to Apple itself with a key from your own Apple developer account (key id,
  team id, bundle id as topic, and the `.p8` key content). Nothing passes through anyone else. This
  requires an app build signed for your own topic, so it is for people who build or distribute their own
  copy of the app. The key can also come from the environment of the Jellyfin process
  (`SILLON_APNS_KEY`, `SILLON_APNS_KEY_ID`, `SILLON_APNS_TEAM_ID`, `SILLON_APNS_TOPIC`), which takes
  precedence over the page. Phones that Apple reports as unregistered are removed.
- **Off.** Nobody is notified.

Installs that predate the relay and already had a key configured stay in direct mode until you change it.

## Security notes

- **Who can request.** Any Jellyfin user who is signed in can search the catalogue and make requests
  (subject to your limits). Administrators see and manage all requests; other users only their own.
  Anonymous callers can only reach the health endpoint and the Lidarr webhook.
- **Webhook.** `POST /Sillon/webhooks/lidarr?secret=...` is protected by a random secret generated on
  first start. Anyone with the link can post events, so keep it private and put Jellyfin behind HTTPS if
  it is reachable from outside.
- **Secrets are write-only.** The Lidarr API key and the APNs key are stored on your server and the
  configuration API only ever returns a mask (`***`). Saving with the mask keeps the stored value.
- **Device tokens belong to one user.** A push token (or relay device key) already registered by
  someone else is never moved to another account: the registration is refused with a 409. A token is
  freed when its owner signs out of the app, or when Apple reports it as no longer valid.
- Admin endpoints require Jellyfin administrator rights.

## Building from source

You need the .NET 10 SDK.

```
dotnet build -warnaserror
dotnet test -c Release
scripts/package.sh        # builds dist/Sillon_<version>.zip (dll and meta.json)
```

Tests cover the request flow, the Lidarr client, the relay protocol (with fixed test vectors), APNs and
the HTTP controllers; they need no Jellyfin server or network. A GitHub Actions workflow runs the same
steps, publishes the zip as a release when a `v<version>` tag is pushed, and updates the repository
manifest on GitHub Pages.

## Contributing and support

Issues and pull requests are welcome on
[GitHub](https://github.com/Overlabing/jellyfin-plugin-sillon/issues). Please do not post API keys, webhook
links or server addresses in issues.

## License

Sillon for Jellyfin is free software under the [GNU General Public License v3.0](LICENSE), the license
of the Jellyfin libraries it builds on.
