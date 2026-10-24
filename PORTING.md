# Jellyfin 12.2 port

This fork starts from j4ckgrey/Baklava commit `1578fc0d295ba7c1fbac0d6b878e81edad6143b1`.
It targets Jellyfin **12.2.0**, **.NET 10**, and Gelato **0.26.28.0**. The original
plugin GUID and configuration schema are retained. This is a compatibility port;
it imports remote Gelato items and does not implement downloads to server storage.

## Changes

- Pin Jellyfin package versions and update the framework and SQLite compile reference.
- Resolve the running Gelato manager from dependency injection rather than constructing
  a second manager. Centralize its optional reflection boundary: current `Stremio`
  field, URI types, metadata/stream/subtitle methods, and import overloads.
- Import approved requests as their original requester, including their Gelato settings.
  Failed imports leave the request pending and return an error.
- Authenticate requests, bind identity on the server, isolate each user's requests,
  and restrict approvals, settings, catalog changes, and cache maintenance to admins.
- Raw stream-cache and web-compatibility inspection APIs are administrator-only because
  their payloads can include source URLs. Keep experimental Baklava caching disabled
  for ordinary users; normal playback uses Gelato and Jellyfin.
- Register the search filter in MVC and support fetch URL/Request inputs. Inject web
  assets without modifying installed Jellyfin files, including when a URL prefix is used.
- Remove tracked Windows `Zone.Identifier` metadata files. Package only `Baklava.dll`,
  `meta.json`, and `thumb.png`; Jellyfin supplies all runtime libraries.

## Build and install

With .NET 10 and PowerShell 7 installed, run from this checkout:

```powershell
./scripts/package.ps1
```

The ZIP is written to `artifacts/baklava_0.9.0.0.zip`. Stop a test Jellyfin server,
extract it into `<data>/plugins/Baklava_0.9.0.0`, then restart and check the plugin
is Active. Keep a backup of existing Baklava configuration when upgrading.
Gelato must be installed and configured with movie/show libraries for approvals.
The request interface is injected into Jellyfin Web; native clients do not load
this interface. File Transformation is not needed by this fork's middleware.

The root `manifest.json` has no installable release until the new ZIP is published.
The generated `artifacts/manifest.json` names the intended release asset. Publish
that exact ZIP before copying the generated manifest to the repository.

## Validation

Download and unpack the official Jellyfin 12.2 Windows server and Gelato 0.26.28 ZIP
into separate directories. After publishing the plugin, run:

```powershell
node scripts/integration-test.mjs --server=<Jellyfin-directory> --gelato=<Gelato-directory> --plugin=<path-to-Baklava.dll>
```

The script starts a disposable loopback-only server, a synthetic addon, and fresh
test accounts. It checks plugin loading, authentication, user isolation, forged
request fields, administration, metadata, catalogs, movie/series imports, streams,
subtitles, and injected web assets. It stops both test processes when finished.
Test data and logs remain under ignored `.build/integration-*` directories.

Real debrid playback, hardware transcoding, and every native client require separate
testing. Gelato's internals may change again; rerun the integration checks after
updating it. This port has not been installed on the production server.
