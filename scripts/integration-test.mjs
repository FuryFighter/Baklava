import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';

// Run against official, unpacked Jellyfin/Gelato binaries; never a user's server.
const args = Object.fromEntries(process.argv.slice(2).map(x => x.split(/=(.*)/s).slice(0, 2)));
assert(args['--server'] && args['--gelato'], 'Pass --server=<unpacked Jellyfin directory> --gelato=<unpacked Gelato directory>');
const root = path.resolve('.build', `integration-${Date.now()}`);
const serverDir = path.resolve(args['--server']);
const plugin = path.resolve(args['--plugin'] || 'artifacts/plugin/Baklava.dll');
const port = Number(args['--port'] || 18097);
const fixturePort = port + 1;
const base = `http://127.0.0.1:${port}`;
const fixtureBase = `http://127.0.0.1:${fixturePort}`;
const password = randomUUID();
const configDir = path.join(root, 'config');
const dataDir = path.join(root, 'data');
const logDir = path.join(root, 'logs');
const fixtureMeta = { id: 'tt1254207', type: 'movie', name: 'Baklava Test Movie', description: 'Synthetic compatibility fixture', releaseInfo: '2008', runtime: '10 min', genres: ['Animation'] };
const seriesMeta = { id: 'tt0000001', type: 'series', name: 'Baklava Test Series', description: 'Synthetic compatibility fixture', releaseInfo: '2026', videos: [{ id: 'tt0000001:1:1', season: 1, episode: 1, name: 'Fixture episode', released: '2026-01-01T00:00:00Z' }] };
const addon = createServer((req, res) => {
  let body;
  if (req.url === '/manifest.json') body = { id: 'org.baklava.test', name: 'Baklava test fixture', version: '1.0.0', types: ['movie', 'series'], resources: ['catalog', 'meta', 'stream', 'subtitles'].map(name => ({ name, types: ['movie', 'series'] })), catalogs: ['movie', 'series'].map(type => ({ type, id: `fixture-${type}`, name: `Fixture ${type}`, extra: [{ name: 'search', isRequired: false }, { name: 'skip', isRequired: false }] })) };
  else if (req.url.startsWith('/catalog/movie/')) body = { metas: [fixtureMeta] };
  else if (req.url.startsWith('/catalog/series/')) body = { metas: [seriesMeta] };
  else if (req.url.startsWith('/meta/movie/')) body = { meta: fixtureMeta };
  else if (req.url.startsWith('/meta/series/')) body = { meta: seriesMeta };
  else if (req.url.startsWith('/stream/')) body = { streams: [{ name: 'Fixture 1080p', title: 'Fixture.1080p.EN.mp4', url: `${fixtureBase}/video.mp4`, behaviorHints: { filename: 'Fixture.1080p.EN.mp4' } }] };
  else if (req.url.startsWith('/subtitles/')) body = { subtitles: [{ id: 'fixture-en', lang: 'eng', url: `${fixtureBase}/subtitle.srt` }] };
  else if (req.url === '/subtitle.srt') { res.end('1\n00:00:00,000 --> 00:00:01,000\nFixture subtitle\n'); return; }
  else { res.writeHead(404); res.end(); return; }
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify(body));
});
await fs.mkdir(configDir, { recursive: true });
await fs.mkdir(logDir, { recursive: true });
await fs.mkdir(path.join(dataDir, 'plugins', 'Baklava_0.9.0.0'), { recursive: true });
await fs.cp(path.resolve(args['--gelato']), path.join(dataDir, 'plugins', 'Gelato_0.26.28.0'), { recursive: true });
if (args['--plugin-dir']) await fs.cp(path.resolve(args['--plugin-dir']), path.join(dataDir, 'plugins', 'Baklava_0.9.0.0'), { recursive: true });
else await fs.copyFile(plugin, path.join(dataDir, 'plugins', 'Baklava_0.9.0.0', 'Baklava.dll'));
await fs.writeFile(path.join(configDir, 'network.xml'), `<?xml version="1.0"?><NetworkConfiguration><BaseUrl></BaseUrl><InternalHttpPort>${port}</InternalHttpPort><PublicHttpPort>${port}</PublicHttpPort><EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>false</EnableRemoteAccess><EnableUPnP>false</EnableUPnP><EnableDiscovery>false</EnableDiscovery><EnableServerDiscovery>false</EnableServerDiscovery><LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses></NetworkConfiguration>`);
await fs.mkdir(path.join(dataDir, 'plugins', 'configurations'), { recursive: true });
await fs.writeFile(path.join(dataDir, 'plugins', 'configurations', 'Gelato.xml'), `<?xml version="1.0"?><PluginConfiguration><Url>${fixtureBase}/manifest.json</Url><MoviePath>${path.join(root, 'movies')}</MoviePath><SeriesPath>${path.join(root, 'series')}</SeriesPath></PluginConfiguration>`);
const output = await fs.open(path.join(logDir, 'process.log'), 'w');
const exe = process.platform === 'win32' ? path.join(serverDir, 'jellyfin.exe') : 'dotnet';
const commandArgs = process.platform === 'win32' ? [] : [path.join(serverDir, 'jellyfin.dll')];
commandArgs.push('--datadir', dataDir, '--configdir', configDir, '--cachedir', path.join(root, 'cache'), '--logdir', logDir, '--webdir', path.join(serverDir, 'jellyfin-web'), '--service', '--ffmpeg', path.join(serverDir, process.platform === 'win32' ? 'ffmpeg.exe' : 'ffmpeg'));
const child = spawn(exe, commandArgs, { windowsHide: true, stdio: ['ignore', output.fd, output.fd] });
let checks = 0;
function check(ok, message) { assert(ok, message); checks++; console.log(`PASS ${message}`); }
async function api(endpoint, { token, method = 'GET', body, status = 200 } = {}) {
  const headers = { Authorization: `MediaBrowser Client="BaklavaTest", Device="Local", DeviceId="baklava-test", Version="1.0"`, 'Content-Type': 'application/json' };
  if (token) headers.Authorization += `, Token="${token}"`;
  const response = await fetch(base + endpoint, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30000) });
  const text = await response.text();
  assert.equal(response.status, status, `${method} ${endpoint}: expected ${status}, got ${response.status}: ${text.slice(0, 400)}`);
  try { return JSON.parse(text); } catch { return text; }
}
try {
  addon.listen(fixturePort, '127.0.0.1');
  await once(addon, 'listening');
  let ready = false;
  for (let i = 0; i < 90; i++) {
    if (child.exitCode !== null) throw new Error(`Jellyfin exited: ${child.exitCode}`);
    try { const info = await api('/System/Info/Public'); if (info.Version) { ready = true; check(info.Version === '12.2.0', 'official Jellyfin 12.2 running'); break; } } catch {}
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  assert(ready, 'Jellyfin startup timed out');
  await api('/Startup/Configuration', { method: 'POST', status: 204, body: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' } });
  await api('/Startup/User');
  await api('/Startup/User', { method: 'POST', status: 204, body: { Name: 'baklava-admin', Password: password } });
  await api('/Startup/Complete', { method: 'POST', status: 204 });
  const admin = await api('/Users/AuthenticateByName', { method: 'POST', body: { Username: 'baklava-admin', Pw: password } });
  assert(admin.AccessToken, `Missing authentication token; response keys: ${Object.keys(admin)}`);
  const users = [];
  for (const name of ['requester-a', 'requester-b']) {
    await api('/Users/New', { method: 'POST', token: admin.AccessToken, body: { Name: name, Password: password } });
    users.push(await api('/Users/AuthenticateByName', { method: 'POST', body: { Username: name, Pw: password } }));
  }
  const [a, b] = users;
  const plugins = await api('/Plugins', { token: admin.AccessToken });
  check(plugins.some(p => p.Name === 'Baklava' && p.Status === 'Active'), 'Baklava loaded as Active');
  check(plugins.some(p => p.Name === 'Gelato' && p.Status === 'Active'), 'Gelato 0.26.28 loaded as Active');
  for (const [name, type, folder] of [['FixtureMovies', 'movies', 'movies'], ['FixtureSeries', 'tvshows', 'series']]) {
    const mediaPath = path.join(root, folder);
    await fs.mkdir(mediaPath, { recursive: true });
    await api(`/Library/VirtualFolders?name=${name}&collectionType=${type}&refreshLibrary=true`, { token: admin.AccessToken, method: 'POST', status: 204, body: { LibraryOptions: { PathInfos: [{ Path: mediaPath }], EnableRealtimeMonitor: false } } });
  }
  // A folder missing during startup is memoized by Gelato; saving settings invalidates it.
  const gelatoConfigRoute = '/Plugins/94ea4e14-8163-4989-96fe-0a2094bc2d6a/Configuration';
  const gelatoConfig = await api(gelatoConfigRoute, { token: admin.AccessToken });
  await api(gelatoConfigRoute, { token: admin.AccessToken, method: 'POST', body: gelatoConfig, status: 204 });
  for (const route of ['/api/baklava/requests', '/api/baklava/config', '/api/baklava/metadata/search?imdbId=tt1254207&itemType=movie', '/api/baklava/metadata/library-status?imdbId=tt1254207&itemType=movie', '/api/baklava/webcompat/streams/fixture']) {
    await api(route, { status: 401 });
  }
  check(true, 'anonymous API requests rejected');
  const metadata = await api(`/api/baklava/metadata/search?imdbId=${fixtureMeta.id}&itemType=movie`, { token: a.AccessToken });
  check(metadata.name === fixtureMeta.name, 'Gelato metadata lookup uses current provider contract');
  const catalogs = await api('/api/baklava/catalogs', { token: admin.AccessToken });
  check(catalogs.length === 2, 'configured addon catalogs listed');
  await api('/api/baklava/catalogs/fixture-movie/library', { token: a.AccessToken, method: 'POST', status: 403, body: { LibraryName: 'Forbidden', MaxItems: 1 } });
  check(true, 'catalog mutations require administrator');
  await api('/api/baklava/streams/raw', { token: a.AccessToken, method: 'POST', status: 403, body: { ItemId: randomUUID(), ImdbId: fixtureMeta.id, ItemType: 'movie', UserId: admin.User.Id } });
  await api('/api/baklava/webcompat/streams/fixture', { token: a.AccessToken, status: 403 });
  check(true, 'raw source URLs and cache inspection require administrator');
  const raw = await api('/api/baklava/streams/raw', { token: admin.AccessToken, method: 'POST', body: { ItemId: randomUUID(), ImdbId: fixtureMeta.id, ItemType: 'movie', UserId: admin.User.Id } });
  check((raw.Streams || raw.streams)?.length === 1, 'stream lookup uses public StremioUri overload');
  const forged = await api('/api/baklava/requests', { token: a.AccessToken, method: 'POST', body: { username: 'baklava-admin', userId: admin.User.Id, id: 'forged-id', title: 'Test Movie', imdbId: fixtureMeta.id, tmdbId: '10378', itemType: 'movie', status: 'approved', approvedBy: 'attacker' } });
  check(forged.username === a.User.Name && forged.userId.replaceAll('-', '') === a.User.Id.replaceAll('-', '') && forged.status === 'pending' && forged.id !== 'forged-id' && !forged.approvedBy, 'request identity and pending status derived on server');
  const mine = await api('/api/baklava/requests', { token: a.AccessToken });
  const others = await api('/api/baklava/requests', { token: b.AccessToken });
  check(mine.some(r => r.id === forged.id) && others.length === 0, 'request lists isolated between users');
  const route = `/api/baklava/requests/${encodeURIComponent(forged.id)}`;
  await api(route, { token: b.AccessToken, method: 'DELETE', status: 403 });
  await api(route, { token: a.AccessToken, method: 'PUT', status: 403, body: { status: 'approved' } });
  await api('/api/baklava/requests/cleanup', { token: a.AccessToken, method: 'POST', status: 403 });
  check(true, 'normal users cannot approve, clean up, or delete another user request');
  await api('/api/baklava/config', { token: a.AccessToken, method: 'PUT', status: 403, body: { disableNonAdminRequests: true } });
  await api('/api/baklava/config', { token: admin.AccessToken, method: 'PUT', body: { disableNonAdminRequests: true } });
  await api('/api/baklava/requests', { token: a.AccessToken, method: 'POST', status: 403, body: { title: 'Blocked', itemType: 'movie' } });
  check(true, 'admin-only settings and disabled-request policy enforced');
  await api('/api/baklava/config', { token: admin.AccessToken, method: 'PUT', body: { disableNonAdminRequests: false } });
  await api(route, { token: admin.AccessToken, method: 'PUT', body: { status: 'approved', approvedBy: 'attacker' } });
  const approved = (await api('/api/baklava/requests', { token: a.AccessToken }))[0];
  check(approved.status === 'approved' && approved.jellyfinId && approved.approvedBy === admin.User.Name, 'approved movie imported using requester configuration');
  const imported = await api(`/Users/${a.User.Id}/Items/${approved.jellyfinId}`, { token: a.AccessToken });
  check(imported.Name === fixtureMeta.name, 'imported request exists in Jellyfin library');
  await api('/api/baklava/config', { token: admin.AccessToken, method: 'PUT', body: { enableExternalSubtitles: true } });
  const subtitles = await api(`/api/baklava/subtitles/fetch?itemId=${approved.jellyfinId}`, { token: a.AccessToken });
  check(JSON.stringify(subtitles).includes('fixture-en'), 'external subtitles resolved with current Gelato contract');
  const seriesRequest = await api('/api/baklava/requests', { token: a.AccessToken, method: 'POST', body: { title: seriesMeta.name, imdbId: seriesMeta.id, tmdbId: '1', itemType: 'series' } });
  await api(`/api/baklava/requests/${encodeURIComponent(seriesRequest.id)}`, { token: admin.AccessToken, method: 'PUT', body: { status: 'approved' } });
  const approvedSeries = (await api('/api/baklava/requests', { token: a.AccessToken })).find(r => r.id === seriesRequest.id);
  check(approvedSeries.status === 'approved' && approvedSeries.jellyfinId, 'series request imported with current InsertMeta signature');
  await api(route, { token: admin.AccessToken, method: 'PUT', body: { status: 'rejected', approvedBy: 'attacker' } });
  const after = await api('/api/baklava/requests', { token: a.AccessToken });
  check(after.find(r => r.id === forged.id).status === 'rejected' && after.find(r => r.id === forged.id).approvedBy === admin.User.Name, 'admin status and approver recorded correctly');
  const html = await api('/web/index.html');
  check(html.includes('<!-- Baklava Injected -->') && html.includes('baklava-requests.js'), 'request UI embedded in Jellyfin web');
  await api(route, { token: a.AccessToken, method: 'DELETE' });
  await api(`/api/baklava/requests/${encodeURIComponent(seriesRequest.id)}`, { token: a.AccessToken, method: 'DELETE' });
  check((await api('/api/baklava/requests', { token: a.AccessToken })).length === 0, 'request owner can delete own request');
  await api('/api/baklava/requests', { token: a.AccessToken, method: 'POST', body: { title: 'UI fixture request', imdbId: fixtureMeta.id, tmdbId: '10378', itemType: 'movie' } });
  await fs.writeFile(path.join(root, 'result.json'), JSON.stringify({ checks, server: '12.2.0', gelato: '0.26.28.0', passed: true }, null, 2));
  console.log(`Integration checks passed: ${checks}. Logs: ${logDir}`);
  if (args['--hold-ui'] === 'true') {
    await fs.writeFile(path.resolve('.build/ui-session.json'), JSON.stringify({ base, username: 'requester-a', password }));
    console.log(`UI fixture ready at ${base}/web/; press Enter to stop it.`);
    await new Promise(resolve => {
      const timeout = setTimeout(resolve, 300000);
      process.stdin.once('data', () => { clearTimeout(timeout); resolve(); });
    });
  }
} finally {
  child.kill();
  await new Promise(resolve => { if (child.exitCode !== null) resolve(); else { child.once('exit', resolve); setTimeout(resolve, 5000).unref(); } });
  addon.closeAllConnections();
  addon.close();
  await output.close();
}
