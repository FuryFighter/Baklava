# Baklava for Jellyfin 12.2

A compatibility fork of [j4ckgrey/Baklava](https://github.com/j4ckgrey/Baklava),
with Jellyfin Web requests and Gelato/AIOStreams integration.

Users submit requests in Jellyfin Web. Administrators approve or reject them.
Approving a request imports the remote title through the requester's Gelato
configuration. Requests and approvals are authenticated on the server.

**This port does not download media files onto server storage.** Choosing an
AIOStreams source and queuing a server download remains a separate feature.

## Requirements

- Jellyfin Server **12.2.0**
- Gelato **0.26.28.0**, configured with an AIOStreams addon and movie/show libraries
- Jellyfin Web for the request interface; native apps still play imported Gelato items
- .NET 10 and PowerShell 7 to build

The middleware injects its own web assets. File Transformation is not required.

## Build and test

```powershell
./scripts/package.ps1
```

The output is `artifacts/baklava_0.9.0.0.zip`. It contains only the plugin assembly,
metadata, and logo; Jellyfin supplies runtime dependencies.

The integration script runs a fresh local Jellyfin instance with synthetic
addon responses and disposable test accounts. It never connects to a production
server or debrid account. See [PORTING.md](PORTING.md) for the full test command,
compatibility changes, and remaining validation limits.

## Install

Stop a test Jellyfin server, extract the ZIP into
`<Jellyfin data>/plugins/Baklava_0.9.0.0`, and restart. Check that Baklava is Active
in the dashboard. Back up existing Baklava settings before upgrading an old install.

The original plugin GUID is retained so existing settings can carry over. Do not
leave two Baklava versions installed at the same time. Refresh the web client
after installing. The raw stream-cache and web-compatibility inspection APIs are
administrator-only; keep experimental Baklava caching disabled for ordinary users.

The root plugin-repository manifest has no installable release yet. The package
script produces a candidate manifest, which should be published only after its
referenced ZIP is available as a release asset.

## Credits and license

Original plugin by **j4ckgrey**, retained under the [MIT license](LICENSE).
This fork ports it to Jellyfin 12.2 and current Gelato APIs.
