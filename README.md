# PostGISApi

Eine Web-API für den Zugriff auf PostGIS-Daten.

Viele PHP-Hostings (und andere Umgebungen) haben keinen PostgreSQL-Treiber. PostGISApi ist ein schlanker Proxy
(ASP.NET Core Minimal API, .NET 10), der PostGIS-Abfragen als REST/GeoJSON-API bereitstellt. Die API läuft als
Docker-Container, zum Beispiel auf einem Raspberry Pi.

- **Layer**: Tabellen und Views mit Geometrie als GeoJSON, mit Filter, BBox, Paging, Sortierung, Reprojektion,
  Vereinfachung, Nearest-Neighbour, räumlichen Abfragen und Mapbox Vector Tiles
- **Schreiben**: Features einfügen, ändern und löschen (pro Layer freischaltbar)
- **Benannte Abfragen**: Parametrisierte SQL-Abfragen, die im Backend definiert werden, als JSON oder GeoJSON
- **Geometrie-Operationen**: transform, buffer, area, length, distance, union, intersection, isvalid usw.
- **Authentisierung**: API-Keys (Header `X-API-Key`) und/oder OAuth 2.0 / OpenID Connect (Bearer-Token)
- **Admin-Backend** unter `/admin`. Die Konfiguration liegt in JSON-Dateien, die auch von Hand bearbeitet werden
  können (Hot-Reload)
- **Swagger UI** unter `/swagger`, OpenAPI-Spezifikation unter `/openapi/v1.json`. Exportierte Dateien liegen in [docs/](docs)

## Schnellstart (Docker / Raspberry Pi)

Voraussetzung: Raspberry Pi mit **64-Bit-OS** (Pi 3/4/5) und Docker. 32-Bit (`linux/arm/v7`) wird ebenfalls unterstützt.

```bash
git clone https://github.com/ELSSwift/PostGISApi.git
cd PostGISApi
```

```bash
docker compose up -d --build
```

Beim ersten Start wird ein Admin-API-Key erzeugt und **einmalig** ins Log geschrieben:

```bash
docker compose logs postgisapi | grep "initial admin key"
```

Danach `http://<pi>:8080/admin` öffnen, mit dem Key anmelden, eine Verbindung anlegen, „Tabellen“ wählen,
dann „Als Layer“. Fertig.

Alternativ einen festen Admin-Key in `.env` setzen (wird nicht ins Repo eingecheckt):

```env
POSTGISAPI_ADMIN_KEY=ein-langer-zufaelliger-wert
DB_PASSWORD=datenbank-passwort
```

Mit lokaler PostGIS-Datenbank inkl. Beispieldaten ([samples/seed.sql](samples/seed.sql)):

```bash
docker compose --profile db up -d --build
```

In der Verbindung dann `host` = `db`, `database` = `gis`, `username` = `gis`, `passwordEnvVar` = `DB_PASSWORD` eintragen.

### Image auf einem PC für den Pi bauen

```bash
docker buildx build --platform linux/arm64 -t postgisapi:latest --load .
```

Der GitHub-Workflow [.github/workflows/docker.yml](.github/workflows/docker.yml) baut bei jedem Push auf `main` ein
Multi-Arch-Image (amd64, arm64, arm/v7) nach `ghcr.io/<owner>/postgisapi`.

## Lokal entwickeln

```bash
dotnet run --project src/PostGISApi
```

Die API läuft auf `http://localhost:5080`, die Konfiguration liegt in `src/PostGISApi/config/` (wird nicht eingecheckt).

## Konfiguration

Alle Einstellungen liegen im Config-Verzeichnis (Docker: Volume `/app/config`). Änderungen über das Backend werden
sofort gespeichert. Von Hand bearbeitete Dateien werden automatisch neu geladen. Ist eine Datei ungültig, bleibt
die alte Konfiguration aktiv und der Fehler steht im Log.

| Datei | Inhalt |
|---|---|
| `connections.json` | Datenbankverbindungen. Das Passwort steht direkt drin oder besser über `passwordEnvVar` in einer Umgebungsvariable |
| `layers.json` | Freigegebene Tabellen/Views: Geometrie- und ID-Spalte, sichtbare Attribute, `readOnly`, `public`, Limits |
| `queries.json` | Benannte SQL-Abfragen mit typisierten Parametern (`@name` im SQL) |
| `security.json` | API-Keys (nur SHA-256-Hash) und OIDC-Einstellungen |
| `settings.json` | CORS, Standard-Verbindung, Swagger an/aus, `?api_key=` erlauben, Tile-Caching |

Beispiele: [samples/config](samples/config).

Umgebungsvariablen / `appsettings.json`:

| Variable | Bedeutung |
|---|---|
| `PostGISApi__ConfigDirectory` | Config-Verzeichnis (Standard `config`, Docker `/app/config`) |
| `PostGISApi__AdminApiKey` | Fester Admin-Key (optional, wird nicht gespeichert) |
| `PostGISApi__TrustForwardedHeaders` | `true` hinter einem Reverse Proxy (nginx, Traefik, Caddy) |

## Authentisierung

**API-Key**: Im Backend erstellen. Der Key wird nur einmal angezeigt. Er wird gesendet als

```http
X-API-Key: pgapi_...
```

oder `Authorization: ApiKey pgapi_...`. Rollen: `read`, `write`, `admin`. Optional lässt sich ein Key auf bestimmte
Layer/Abfragen beschränken und mit einem Ablaufdatum versehen.

**OAuth 2.0 / OpenID Connect**: Im Backend unter „OIDC“ Authority (Issuer-URL) und Audience eintragen. Clients senden
`Authorization: Bearer <access_token>`. Die Claim-Werte (z.B. `roles`, `groups` oder bei Keycloak
`realm_access.roles`, optional auch `scope`) werden auf die internen Rollen `read`/`write`/`admin` abgebildet.
Änderungen gelten sofort.

Layer und Abfragen mit `public: true` sind ohne Anmeldung lesbar.

## API-Übersicht

| Methode | Pfad | Beschreibung |
|---|---|---|
| GET | `/api/v1/layers` | Sichtbare Layer |
| GET | `/api/v1/layers/{layer}` | Metadaten (SRID, Geometrietyp, Attribute) |
| GET | `/api/v1/layers/{layer}/features` | GeoJSON FeatureCollection |
| GET | `/api/v1/layers/{layer}/features/{id}` | Einzelnes Feature |
| POST | `/api/v1/layers/{layer}/query` | Räumliche Abfrage mit Eingabegeometrie |
| GET | `/api/v1/layers/{layer}/count` | Anzahl |
| GET | `/api/v1/layers/{layer}/extent` | Ausdehnung (BBox) |
| GET | `/api/v1/layers/{layer}/nearest` | k nächste Features (`_distance` in Metern) |
| GET | `/api/v1/layers/{layer}/tiles/{z}/{x}/{y}.mvt` | Vector Tiles (EPSG:3857) |
| POST / PUT / DELETE | `/api/v1/layers/{layer}/features[/{id}]` | Schreiben (Rolle `write`, Layer nicht `readOnly`) |
| GET / POST | `/api/v1/queries/{name}` | Benannte Abfrage ausführen |
| POST | `/api/v1/geometry/{operation}` | Geometrie-Operation |
| GET | `/health`, `/health/db` | Health-Checks |
| * | `/api/admin/...` | Konfiguration (Rolle `admin`), siehe `/openapi/admin.json` |

Parameter von `GET /features`: `bbox=minx,miny,maxx,maxy`, `bboxSrid`, `filter=spalte:op:wert` (mehrfach;
`eq, neq, lt, lte, gt, gte, like, ilike, in, isnull, notnull`), `properties=a,b`, `orderBy`, `desc`, `limit`, `offset`,
`srid` (Ausgabe, Standard 4326), `precision`, `simplify`, `geometry=false`, `count=true`.

### Beispiele

```bash
curl -H "X-API-Key: $KEY" "http://pi:8080/api/v1/layers/staedte/features?filter=kanton:eq:BE&orderBy=einwohner&desc=true&limit=5"
```

```bash
curl -H "X-API-Key: $KEY" "http://pi:8080/api/v1/layers/staedte/nearest?x=7.45&y=46.95&k=3"
```

```bash
curl -H "X-API-Key: $KEY" -H "Content-Type: application/json" -d '{"spatial":{"geometry":{"type":"Point","coordinates":[7.45,46.95]},"relation":"intersects"}}' http://pi:8080/api/v1/layers/gemeinden/query
```

```bash
curl -H "X-API-Key: $KEY" -H "Content-Type: application/json" -d '{"geometry":{"type":"Point","coordinates":[7.4474,46.948]},"targetSrid":2056}' http://pi:8080/api/v1/geometry/transform
```

### PHP

```php
<?php
$ctx = stream_context_create(['http' => ['header' => "X-API-Key: " . getenv('POSTGISAPI_KEY')]]);
$url = 'https://gis.example.ch/api/v1/layers/staedte/features?' . http_build_query([
    'bbox' => '7,46.5,8,47.2',
    'properties' => 'name,einwohner',
]);
$geojson = json_decode(file_get_contents($url, false, $ctx), true);
foreach ($geojson['features'] as $f) {
    echo $f['properties']['name'], PHP_EOL;
}
```

```php
<?php
// benannte Abfrage
$ch = curl_init('https://gis.example.ch/api/v1/queries/staedte_kanton');
curl_setopt_array($ch, [
    CURLOPT_POST => true,
    CURLOPT_RETURNTRANSFER => true,
    CURLOPT_HTTPHEADER => ['Content-Type: application/json', 'X-API-Key: ' . getenv('POSTGISAPI_KEY')],
    CURLOPT_POSTFIELDS => json_encode(['kanton' => 'BE', 'min' => 50000]),
]);
$result = json_decode(curl_exec($ch), true);
```

### Leaflet / MapLibre (Vector Tiles)

```js
// MapLibre GL – öffentlicher Layer
map.addSource('staedte', { type: 'vector', tiles: ['https://gis.example.ch/api/v1/layers/staedte/tiles/{z}/{x}/{y}.mvt'] });
map.addLayer({ id: 'staedte', type: 'circle', source: 'staedte', 'source-layer': 'staedte' });
```

Für nicht-öffentliche Layer in Karten-Clients kann in den Einstellungen `?api_key=` erlaubt werden. Dafür einen
eigenen Key mit Rolle `read`, eingeschränkt auf die betreffenden Layer, verwenden.

## Sicherheit

- Clients senden nie SQL. Tabellen- und Spaltennamen kommen ausschliesslich aus der Konfiguration bzw. werden gegen
  die Spaltenliste des Layers geprüft. Alle Werte werden als Parameter übergeben.
- Benannte Abfragen laufen in einer `READ ONLY`-Transaktion, sofern `allowWrite` nicht gesetzt ist.
- Für die Verbindung am besten einen eigenen DB-Benutzer mit minimalen Rechten verwenden (nur `SELECT` bzw. die nötigen
  Schreibrechte).
- Im Internet die API hinter einem Reverse Proxy mit TLS betreiben.

## Projektstruktur

```
src/PostGISApi/
  Configuration/   Config-Modelle, ConfigStore (JSON-Dateien, Validierung, Hot-Reload)
  Data/            Npgsql-Datenquellen, Layer-Metadaten, Feature-/Geometrie-/Query-Services (SQL-Generierung)
  Auth/            API-Key-Handler, OIDC (JWT Bearer), Rollen und Zugriffsprüfung
  Endpoints/       Minimal-API-Endpoints (öffentlich, Admin, Health)
  OpenApi/         OpenAPI-Dokumente und Security-Schemes
  wwwroot/admin/   Admin-Backend (HTML/JS)
docs/              Exportierte OpenAPI-Spezifikation
samples/           Beispieldaten (seed.sql) und Beispielkonfiguration
```
