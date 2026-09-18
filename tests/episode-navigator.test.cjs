const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const nav = require('../src/Emby.Plugins.Bangumi/Web/Assets/episode-navigator.js');
const navigatorSource = fs.readFileSync(require.resolve('../src/Emby.Plugins.Bangumi/Web/Assets/episode-navigator.js'), 'utf8');
const episode = (n, user = {}, extra = {}) => ({ Id: String(n), Type: 'Episode', IndexNumber: n, UserData: user, ...extra });

test('Fresh: old unmarked episodes do not pull progress back to episode 1', () => {
    const items = Array.from({ length: 50 }, (_, i) => episode(i + 1, { Played: i >= 21 && i <= 31 }));
    assert.equal(nav.recommendation(items).Id, '33');
});
test('resume unfinished episode; ignore residual position on an already watched episode', () => {
    const items = [episode(25, { Played: true, PlaybackPositionTicks: 100 }),
        episode(32, { Played: true }), episode(33, { PlaybackPositionTicks: 80 })];
    assert.equal(nav.recommendation(items).Id, '33');
});
test('most recent unfinished replay wins over older unfinished episodes', () => {
    const items = [episode(8, { PlaybackPositionTicks: 80, LastPlayedDate: '2026-09-18T12:00:00Z' }),
        episode(33, { PlaybackPositionTicks: 90, LastPlayedDate: '2026-09-17T12:00:00Z' })];
    assert.equal(nav.recommendation(items).Id, '8');
});
test('manual location survives reload until playback changes', () => {
    const items = [episode(1), episode(2), episode(3)];
    const saved = { itemId: '3', progress: nav.fingerprint(items) };
    assert.equal(nav.selectInitial(items, saved).Id, '3');
    items[0].UserData.Played = true;
    assert.equal(nav.selectInitial(items, saved).Id, '2');
});
test('episode detail page selects that episode even when another is recommended', () => {
    const items = [episode(1), episode(2), episode(3, { Played: true })];
    assert.equal(nav.selectInitial(items, null, '2').Id, '2');
});
test('season browsing is remembered until playback moves to another season', () => {
    const items = [episode(1, { Played: true }, { SeasonId: 's1' }),
        episode(2, {}, { SeasonId: 's2', ParentIndexNumber: 2 })];
    const saved = { seasonId: 's1', progress: nav.fingerprint(items) };
    assert.equal(nav.initialSeason(items, saved), 's1');
    items[1].UserData = { PlaybackPositionTicks: 90, LastPlayedDate: '2026-09-18T12:00:00Z' };
    assert.equal(nav.initialSeason(items, saved), 's2');
    assert.equal(nav.initialSeason(items, saved, 's1'), 's1');
});
test('missing saved item gracefully follows progress', () => {
    const items = [episode(4, { Played: true }), episode(6)];
    assert.equal(nav.selectInitial(items, { itemId: '5', progress: nav.fingerprint(items) }).Id, '6');
});
test('mode change keeps the same episode within its new page', () => {
    const items = Array.from({ length: 156 }, (_, i) => episode(i + 1));
    assert.equal(nav.pageOf(items, '131', 'cards'), 10);
    assert.equal(nav.pageOf(items, '131', 'numbers'), 2);
    assert.equal(nav.pageOf(items, '131', 'cards'), 10);
});
test('jump understands combined episodes and gaps without guessing', () => {
    const items = [episode(1, {}, { IndexNumberEnd: 2 }), episode(4), episode(4.5)];
    assert.equal(nav.findNumber(items, '2').Id, '1');
    assert.equal(nav.findNumber(items, '04.5').Id, '4.5');
    for (const value of ['3', '', '-1', 'NaN', '4a']) assert.equal(nav.findNumber(items, value), null);
});
test('normalization excludes virtual placeholders, retains numbering and sorts numerically', () => {
    const items = [episode(10), episode(2), episode(3, {}, { IsMissing: true }),
        episode(4, {}, { LocationType: 'Virtual' }), episode(5, {}, { Type: 'Season' }), episode(2)];
    assert.deepEqual(nav.normalize(items).map(e => e.IndexNumber), [2, 10]);
});
test('all watched does not jump back to the first episode; empty list is safe', () => {
    assert.equal(nav.recommendation([episode(1, { Played: true }), episode(2, { Played: true })]).Id, '2');
    assert.equal(nav.recommendation([]), null);
});

test('episode cards use Emby playback actions for one-click playback', () => {
    assert.match(navigatorSource, /Emby\.importModule\("\.\/modules\/common\/playback\/playbackactions\.js"\)/);
    assert.match(navigatorSource, /var actions = module && module\.default \? module\.default : module;/);
    assert.match(navigatorSource, /actions\.play\(\{ items: \[playbackItem\(ctx, item\)\], fullscreen: true \}\)/);
    assert.match(navigatorSource, /node\("article", "bgmui-epCard/);
    assert.match(navigatorSource, /e\.preventDefault\(\); e\.stopPropagation\(\); playEpisode\(ctx, item, link\)/);
});

test('episode details remain a separate native route', () => {
    assert.match(navigatorSource, /var details = node\("a", "bgmui-epDetails", "详情"\)/);
    assert.match(navigatorSource, /details\.href = itemHref\(ctx, item\)/);
    assert.match(navigatorSource, /details\.addEventListener\("click", function \(e\) \{ e\.stopPropagation\(\); \}\)/);
});
