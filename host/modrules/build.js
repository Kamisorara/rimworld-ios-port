// Turns RimSort's community load-order rules (communityRules.json on GitHub) into the compact text file the
// phone interface library reads (ModRules.txt; the game can also download and convert the JSON itself).
// usage: node build.js [--offline]     downloads the JSON unless --offline (then the cached copy next to this file is used)
//
// Line format (tab separated, package ids in lower case):
//   <packageId>	after=<id>,<id>	before=<id>	top	bottom	incompatible=<id>
// First line: "# rules <unix timestamp of the database> <date>".
const fs = require('fs'), path = require('path');
const { execFileSync } = require('child_process');
const URL = 'https://raw.githubusercontent.com/RimSort/Community-Rules-Database/main/communityRules.json';
const here = __dirname;
const cached = path.join(here, 'communityRules.json');
if (!process.argv.includes('--offline')) {
	try {
		execFileSync('curl', ['-sL', '--max-time', '60', '-o', cached + '.new', URL], { stdio: 'inherit' });
		const text = fs.readFileSync(cached + '.new', 'utf8');
		if (!text.startsWith('{')) throw new Error('the download is not JSON');
		fs.renameSync(cached + '.new', cached);
	} catch (e) {
		console.error('download failed (' + e.message.split('\n')[0] + '), using the cached copy');
		if (fs.existsSync(cached + '.new')) fs.unlinkSync(cached + '.new');
	}
}
if (!fs.existsSync(cached)) {
	console.error('no cached communityRules.json next to this script and no download: nothing to convert');
	process.exit(1);
}
const db = JSON.parse(fs.readFileSync(cached, 'utf8'));
const rules = db.rules || {};
function ids(value) {
	// loadAfter / loadBefore / incompatibleWith: an object keyed by package id (or, defensively, an array of ids).
	if (!value) return [];
	if (Array.isArray(value)) return value.map(v => String(v).toLowerCase());
	if (typeof value === 'object') return Object.keys(value).map(k => k.toLowerCase());
	return [String(value).toLowerCase()];
}
function flag(value) {
	// loadTop / loadBottom: true, or an object such as {"value": true, "comment": ...}.
	if (value === true) return true;
	if (value && typeof value === 'object') return value.value === true || value.value === 'true' || (value.value === undefined && Object.keys(value).length > 0);
	return value === 'true';
}
const lines = ['# rules ' + (db.timestamp || 0) + ' ' + new Date((db.timestamp || 0) * 1000).toISOString().slice(0, 10)];
let n = 0;
for (const pkg of Object.keys(rules).sort()) {
	const r = rules[pkg];
	const parts = [pkg.toLowerCase()];
	const after = ids(r.loadAfter).filter(x => x !== pkg.toLowerCase());
	const before = ids(r.loadBefore).filter(x => x !== pkg.toLowerCase());
	const incompatible = ids(r.incompatibleWith);
	if (after.length) parts.push('after=' + after.join(','));
	if (before.length) parts.push('before=' + before.join(','));
	if (flag(r.loadTop)) parts.push('top');
	if (flag(r.loadBottom)) parts.push('bottom');
	if (incompatible.length) parts.push('incompatible=' + incompatible.join(','));
	if (parts.length === 1) continue;
	lines.push(parts.join('\t'));
	n++;
}
const out = path.join(here, 'ModRules.txt');
fs.writeFileSync(out, lines.join('\n') + '\n');
console.log(`${out}: ${n} mods with rules, database of ${lines[0].slice(8)}`);
