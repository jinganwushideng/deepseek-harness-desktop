#!/usr/bin/env node

import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const outputArgument = process.argv.slice(2).find(value => !value.startsWith('--'));
const outputPath = resolve(outputArgument || 'catalog/plugin-index.json');
const githubToken = process.env.GITHUB_TOKEN || '';
const userAgent = 'deepseek-harness-desktop-catalog/1.1';
const npmQueries = [
  'deepseek-harness', 'deepseek harness plugin', 'dsh-plugin', 'dsh plugin',
  'keywords:deepseek-harness', 'keywords:dsh-plugin', 'deepseek harness theme', 'dsh theme',
  '@deepseek-ai/dsh plugin', 'harness skin'
];
const bootstrapPackages = ['@dshthemes/ui', 'dsh-theme-plugin'];
const githubQueries = [
  'deepseek harness plugin in:name,description,readme',
  'deepseek harness theme in:name,description,readme',
  'dsh plugin in:name,description,readme',
  'dsh theme in:name,description,readme',
  'topic:deepseek-harness', 'topic:dsh-plugin', 'topic:dsh-theme'
];
const githubCodeQueries = [
  '"@deepseek-ai/dsh" filename:package.json'
];
const chineseReadmeNames = [
  'README.zh-CN.md', 'README.zh_CN.md', 'README.zh.md', 'README_CN.md',
  'README-CN.md', 'README_zh.md', 'README-cn.md', 'docs/README.zh-CN.md',
  'docs/README.zh.md', 'docs/zh-CN/README.md', 'docs/zh/README.md', 'doc/README.zh-CN.md'
];

const delay = milliseconds => new Promise(resolveDelay => setTimeout(resolveDelay, milliseconds));

const request = async (url, options = {}) => {
  const headers = { 'User-Agent': userAgent, ...options.headers };
  let response;
  for (let attempt = 0; attempt < 4; attempt++) {
    response = await fetch(url, { ...options, headers, signal: options.signal || AbortSignal.timeout(20000) });
    if (response.status !== 429 && response.status < 500) return response;
    const retryAfter = Number(response.headers.get('retry-after'));
    await delay(Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : 700 * (2 ** attempt));
  }
  return response;
};

const json = async (url, options = {}) => {
  const response = await request(url, { ...options, headers: { Accept: 'application/json', ...options.headers } });
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}: ${url}`);
  return response.json();
};

const text = async url => {
  const response = await request(url);
  if (!response.ok) return '';
  return response.text();
};

const compactMarkdown = markdown => String(markdown || '')
  .replace(/```[\s\S]*?```/g, ' ')
  .replace(/<!--([\s\S]*?)-->/g, ' ')
  .replace(/<script[\s\S]*?<\/script>/gi, ' ');

const chineseSummary = markdown => {
  const clean = compactMarkdown(markdown);
  const paragraphs = clean.split(/\n\s*\n/).map(block => block
    .replace(/<[^>]+>|!\[[^\]]*\]\([^)]*\)|\[([^\]]+)\]\([^)]*\)|[`#>*_|~-]/g, '$1 ')
    .replace(/\s+/g, ' ').trim());
  const candidates = paragraphs.filter(value => {
    const chinese = (value.match(/[\u3400-\u9fff]/g) || []).length;
    return chinese >= 8 && value.length >= 18 && value.length <= 700 &&
      !/^(目录|导航|安装|使用|文档|贡献|许可证|开源协议|English|README)/i.test(value) &&
      !/license|copyright|徽章|交流群|QQ群|微信群|[銆鍔鍚绔浠鏂鍙锛璇姹]/i.test(value);
  });
  const scored = candidates.map((value, index) => ({
    value,
    score: (/简介|介绍|概述|关于|是什么|功能/.test(value) ? 80 : 0) + Math.min((value.match(/[\u3400-\u9fff]/g) || []).length, 80) - index
  })).sort((a, b) => b.score - a.score);
  return (scored[0]?.value || '').slice(0, 600);
};

const categoryOf = (manifest, extra = '') => {
  const identity = JSON.stringify({ name: manifest.name, keywords: manifest.keywords, extra }).toLowerCase();
  const description = String(manifest.description || '').toLowerCase();
  if (/theme|skin|appearance|wallpaper|webui-background|any-background|ui-theme|ui-skins|主题|皮肤|壁纸/.test(identity) || /\btheme\b|\bskin\b|appearance|wallpaper|background (image|video)|configurable (web )?ui background|主题插件|皮肤插件|壁纸|界面背景/.test(description)) return 'Skin';
  const combined = `${identity} ${description}`;
  if (/skill\.md|"skills"|dsh-skill/.test(combined)) return 'Skill';
  if (/devtool|developer|debug|inspect|modlens|scaffold/.test(combined)) return 'DeveloperTool';
  return 'Plugin';
};

const hasHarnessManifest = manifest => {
  const dsh = manifest?.dsh;
  return Boolean(dsh && typeof dsh === 'object' && (dsh.client || dsh.bundle));
};

const lifecycle = manifest => {
  const scripts = manifest?.scripts || {};
  return ['preinstall', 'install', 'postinstall', 'prepare'].some(name => typeof scripts[name] === 'string');
};

const repositoryUrl = value => {
  let url = typeof value === 'string' ? value : value?.url || '';
  url = url.replace(/^git\+/, '').replace(/^git:\/\//, 'https://').replace(/\.git$/, '');
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'https:' && ['github.com', 'gitlab.com', 'gitee.com', 'bitbucket.org', 'codeberg.org'].includes(parsed.hostname.toLowerCase()) ? url : '';
  } catch { return ''; }
};

const safeId = value => value.toLowerCase().replace(/^@/, '').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
const githubHeaders = () => githubToken ? { Authorization: `Bearer ${githubToken}`, 'X-GitHub-Api-Version': '2022-11-28' } : {};

const mapPool = async (values, concurrency, worker) => {
  const queue = [...values];
  const results = [];
  const runners = Array.from({ length: Math.min(concurrency, queue.length) }, async () => {
    while (queue.length) {
      const value = queue.shift();
      const result = await worker(value);
      if (result) results.push(result);
    }
  });
  await Promise.all(runners);
  return results;
};

const chineseReadmeLink = markdown => {
  const links = [...String(markdown || '').matchAll(/\[([^\]]+)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g)];
  return links.map(match => ({ label: match[1], path: match[2] }))
    .find(link => /中文|简体|Chinese|zh[-_]?cn|readme[-_.]?(?:zh|cn)/i.test(`${link.label} ${link.path}`))?.path || '';
};

const rankChineseReadme = path => {
  const lower = path.toLowerCase();
  if (!/\.md$/.test(lower) || !/(readme|介绍|简介)/i.test(lower) || !/(zh|cn|chinese|中文)/i.test(lower)) return -1;
  return (lower.includes('zh-cn') || lower.includes('zh_cn') ? 100 : 0) + (lower.startsWith('readme') ? 40 : 0) - lower.split('/').length;
};

async function npmReadmes(name, version) {
  const packageRoot = `https://unpkg.com/${name}@${version}/`;
  const defaultReadme = await text(`${packageRoot}README.md`).catch(() => '');
  const candidates = [];
  let inventoryLoaded = false;
  const linked = chineseReadmeLink(defaultReadme);
  if (linked) candidates.push(linked);
  try {
    const meta = await json(`${packageRoot}?meta`);
    inventoryLoaded = true;
    candidates.push(...(meta.files || []).map(file => String(file.path || '').replace(/^\//, ''))
      .filter(path => rankChineseReadme(path) >= 0).sort((a, b) => rankChineseReadme(b) - rankChineseReadme(a)));
  } catch { }
  if (!inventoryLoaded) candidates.push(...chineseReadmeNames.slice(0, 6));
  const found = await mapPool([...new Set(candidates)].slice(0, 8), 4, async candidate => {
    try { const content = await text(new URL(candidate, packageRoot).href); return (content.match(/[\u3400-\u9fff]/g) || []).length >= 12 ? { candidate, content } : null; }
    catch { return null; }
  });
  if (found[0]) return { defaultReadme, chineseReadme: found[0].content, chinesePath: found[0].candidate, packageRoot };
  return { defaultReadme, chineseReadme: '', chinesePath: '', packageRoot };
}

async function githubReadmes(repo, branch) {
  const rawRoot = `https://raw.githubusercontent.com/${repo}/${encodeURIComponent(branch)}/`;
  const defaultReadme = await text(`${rawRoot}README.md`).catch(() => '');
  const candidates = [];
  let inventoryLoaded = false;
  const linked = chineseReadmeLink(defaultReadme);
  if (linked) candidates.push(linked);
  if (githubToken) {
    try {
      const tree = await json(`https://api.github.com/repos/${repo}/git/trees/${encodeURIComponent(branch)}?recursive=1`, { headers: githubHeaders() });
      inventoryLoaded = true;
      candidates.push(...(tree.tree || []).map(entry => entry.path).filter(path => rankChineseReadme(path) >= 0)
        .sort((a, b) => rankChineseReadme(b) - rankChineseReadme(a)));
    } catch { }
  }
  if (!inventoryLoaded) candidates.push(...chineseReadmeNames.slice(0, 6));
  const found = await mapPool([...new Set(candidates)].slice(0, 8), 4, async candidate => {
    try { const content = await text(new URL(candidate.replace(/^\.\//, ''), rawRoot).href); return (content.match(/[\u3400-\u9fff]/g) || []).length >= 12 ? { candidate, content } : null; }
    catch { return null; }
  });
  if (found[0]) return { defaultReadme, chineseReadme: found[0].content, chinesePath: found[0].candidate, rawRoot };
  return { defaultReadme, chineseReadme: '', chinesePath: '', rawRoot };
}

const resolveMedia = (src, base, githubRepo = '', branch = '') => {
  try {
    let value = String(src || '').trim().replace(/^<|>$/g, '').replace(/&amp;/g, '&');
    if (!value || value.startsWith('data:')) return '';
    if (value.startsWith('/') && githubRepo) value = `https://raw.githubusercontent.com/${githubRepo}/${encodeURIComponent(branch)}${value}`;
    else value = new URL(value, base).href;
    value = value.replace(/^https:\/\/github\.com\/([^/]+\/[^/]+)\/blob\//i, 'https://raw.githubusercontent.com/$1/');
    const parsed = new URL(value);
    if (parsed.protocol !== 'https:' || !/\.(?:png|jpe?g|gif|bmp)(?:$|[?#])/i.test(parsed.pathname + parsed.search)) return '';
    return parsed.href;
  } catch { return ''; }
};

const previewImage = (markdown, base, githubRepo = '', branch = '') => {
  const source = compactMarkdown(markdown);
  const candidates = [];
  for (const match of source.matchAll(/!\[([^\]]*)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g)) candidates.push({ alt: match[1], src: match[2], index: match.index || 0 });
  for (const match of source.matchAll(/<img\b[^>]*?src=["']([^"']+)["'][^>]*>/gi)) {
    const alt = match[0].match(/alt=["']([^"']*)["']/i)?.[1] || '';
    candidates.push({ alt, src: match[1], index: match.index || 0 });
  }
  return candidates.map(candidate => {
    const identity = `${candidate.alt} ${candidate.src}`.toLowerCase();
    const url = resolveMedia(candidate.src, base, githubRepo, branch);
    const score = (/screenshot|preview|demo|showcase|效果|预览|界面|截图|主页|cover/.test(identity) ? 100 : 0) -
      (/badge|shield|logo|icon|avatar|coverage|build|npm|license|wechat|qq|二维码/.test(identity) ? 180 : 0) - candidate.index / 100000;
    return { url, score };
  }).filter(candidate => candidate.url && candidate.score > -100).sort((a, b) => b.score - a.score)[0]?.url || '';
};

const githubOpenGraphPreview = repo => repo ? `https://opengraph.githubassets.com/1/${repo}` : '';

const plausibleNpmResult = row => {
  const packageData = row.package || {};
  const name = String(packageData.name || '').toLowerCase();
  const description = String(packageData.description || '').toLowerCase();
  const keywords = (packageData.keywords || []).map(value => String(value).toLowerCase());
  return !name.startsWith('@deepseek-ai/') && (
    /(^|[/@-])dsh(?:[-/]|$)|deepseek[-_]?harness|harness[-_]?deepseek/.test(name) ||
    keywords.some(value => /^(deepseek-harness|dsh-plugin|dsh-theme|dsh-skin)$/.test(value)) ||
    /deepseek harness/.test(description)
  );
};

async function npmCandidates(previousMap, previousCursor = 0) {
  const discovery = new Map();
  const rank = new Map();
  const add = (name, source, score = 0) => {
    if (!name || name.startsWith('@deepseek-ai/')) return;
    if (!discovery.has(name)) discovery.set(name, new Set());
    discovery.get(name).add(source);
    rank.set(name, Math.max(rank.get(name) || 0, Number(score) || 0));
  };
  bootstrapPackages.forEach(name => add(name, '引导包'));
  previousMap.forEach((_, name) => add(name, '上次索引'));
  for (const query of npmQueries) {
    for (const from of [0, 250]) {
      try {
        const result = await json(`https://registry.npmjs.org/-/v1/search?size=250&from=${from}&text=${encodeURIComponent(query)}`);
        for (const row of result.objects || []) if (plausibleNpmResult(row)) add(row.package.name, `npm:${query}`, row.score?.final);
        if ((result.objects || []).length < 250) break;
        await delay(650);
      } catch (error) { process.stderr.write(`npm search skipped (${query}/${from}): ${error.message}\n`); break; }
    }
  }
  process.stdout.write(`npm discovery: ${discovery.size} plausible candidates.\n`);

  const manifests = new Map();
  await mapPool([...discovery.keys()].sort(), 6, async name => {
    try {
      const manifest = await json(`https://unpkg.com/${name}@latest/package.json`);
      if (hasHarnessManifest(manifest)) manifests.set(name, manifest);
    } catch (error) { process.stderr.write(`npm manifest skipped (${name}): ${error.message}\n`); }
  });

  // One dependency hop catches bundles and companion packages that npm search
  // does not rank for Harness-specific keywords.
  for (const [owner, manifest] of manifests) {
    const dependencies = { ...manifest.dependencies, ...manifest.optionalDependencies, ...manifest.peerDependencies };
    for (const name of Object.keys(dependencies || {})) if (!name.startsWith('@deepseek-ai/') && /dsh|harness|plugin|theme|skin/i.test(name)) add(name, `依赖:${owner}`);
  }
  const extra = [...discovery.keys()].filter(name => !manifests.has(name));
  await mapPool(extra, 6, async name => {
    try {
      const manifest = await json(`https://unpkg.com/${name}@latest/package.json`);
      if (hasHarnessManifest(manifest)) manifests.set(name, manifest);
    } catch { }
  });
  process.stdout.write(`npm verification: ${manifests.size} Harness manifests.\n`);

  const ordered = [...manifests.entries()].sort(([left], [right]) =>
    (previousMap.get(right)?.popularity || rank.get(right) || 0) - (previousMap.get(left)?.popularity || rank.get(left) || 0) || left.localeCompare(right));
  const requestedLimit = Number(process.env.CATALOG_ENRICH_LIMIT || 100);
  const enrichLimit = Math.max(10, Math.min(Number.isFinite(requestedLimit) ? requestedLimit : 100, 200));
  const cursor = ordered.length ? Math.max(0, previousCursor) % ordered.length : 0;
  const enrichNames = new Set(Array.from({ length: Math.min(enrichLimit, ordered.length) }, (_, offset) => ordered[(cursor + offset) % ordered.length][0]));
  const nextCursor = ordered.length ? (cursor + enrichNames.size) % ordered.length : 0;
  process.stdout.write(`metadata enrichment: ${enrichNames.size}/${ordered.length}, cursor ${cursor} -> ${nextCursor}.\n`);

  const items = await mapPool(ordered, 6, async ([name, manifest]) => {
    try {
      const old = previousMap.get(name);
      const shouldEnrich = enrichNames.has(name);
      let popularity = old?.popularity || Math.round((rank.get(name) || 0) * 1000);
      let updatedAt = manifest.date || old?.updatedAt || new Date(0).toISOString();
      const docs = shouldEnrich ? await npmReadmes(name, manifest.version) : { defaultReadme: '', chineseReadme: '', chinesePath: '', packageRoot: `https://unpkg.com/${name}@${manifest.version}/` };
      let descriptionZh = /[\u3400-\u9fff]/.test(manifest.description || '') ? manifest.description : (old?.descriptionZh || '');
      if (shouldEnrich) descriptionZh = chineseSummary(docs.chineseReadme || docs.defaultReadme) || descriptionZh;
      let descriptionSource = descriptionZh ? (/[\u3400-\u9fff]/.test(manifest.description || '') ? 'package.json 中文描述' : docs.chinesePath || old?.descriptionSource || 'README.md') : '';
      if (shouldEnrich) {
        try {
          const downloads = await json(`https://api.npmjs.org/downloads/point/last-month/${encodeURIComponent(name)}`);
          popularity = Number(downloads.downloads) || popularity;
        } catch { }
      }
      const repoUrl = repositoryUrl(manifest.repository);
      const githubMatch = repoUrl.match(/^https:\/\/github\.com\/([^/]+\/[^/#]+)$/i);
      let githubDocs = null;
      let branch = 'main';
      if (shouldEnrich && githubToken && githubMatch) {
        try {
          const repo = await json(`https://api.github.com/repos/${githubMatch[1]}`, { headers: githubHeaders() });
          popularity = Math.max(popularity, (Number(repo.stargazers_count) || 0) * 100);
          updatedAt = repo.updated_at || updatedAt;
          branch = repo.default_branch || branch;
          githubDocs = await githubReadmes(githubMatch[1], branch);
          if (!descriptionZh) descriptionZh = chineseSummary(githubDocs.chineseReadme || githubDocs.defaultReadme);
        } catch { }
      }
      const previewImageUrl = (shouldEnrich ? previewImage(docs.chineseReadme, docs.packageRoot) || previewImage(docs.defaultReadme, docs.packageRoot) ||
        (githubMatch && githubDocs ? previewImage(githubDocs.chineseReadme, githubDocs.rawRoot, githubMatch[1], branch) || previewImage(githubDocs.defaultReadme, githubDocs.rawRoot, githubMatch[1], branch) : '')
        : '') || old?.previewImageUrl || (githubMatch ? githubOpenGraphPreview(githubMatch[1]) : '');
      return {
        id: safeId(name), name: manifest.dsh?.displayName || manifest.name,
        description: String(manifest.description || '').slice(0, 600), descriptionZh: String(descriptionZh || '').slice(0, 600),
        descriptionSource, installSpec: `${name}@latest`, package: name, version: manifest.version || '',
        repositoryUrl: repoUrl, previewImageUrl, sourceType: 'npm', license: manifest.license || '', category: categoryOf(manifest),
        verified: true, hasLifecycleScripts: lifecycle(manifest), requiresBuildApproval: false, popularity, updatedAt,
        discoverySource: [...(discovery.get(name) || [])].slice(0, 3).join(' · ')
      };
    } catch (error) {
      process.stderr.write(`npm candidate skipped (${name}): ${error.message}\n`);
      return previousMap.get(name) || null;
    }
  });
  return { items, nextCursor };
}

async function githubCandidates(existingPackages) {
  if (!githubToken) { process.stderr.write('GITHUB_TOKEN is absent; GitHub discovery was skipped.\n'); return []; }
  const repositories = new Map();
  const addRepo = (repo, source) => {
    if (!repo?.full_name || repo.owner?.login === 'deepseek-ai') return;
    const current = repositories.get(repo.full_name) || { repo, sources: new Set() };
    current.sources.add(source); repositories.set(repo.full_name, current);
  };
  for (const query of githubQueries) {
    for (const page of [1, 2]) {
      try {
        const result = await json(`https://api.github.com/search/repositories?per_page=100&page=${page}&sort=updated&q=${encodeURIComponent(query)}`, { headers: githubHeaders() });
        for (const repo of result.items || []) addRepo(repo, `GitHub:${query}`);
        if ((result.items || []).length < 100) break;
      } catch (error) { process.stderr.write(`GitHub repository search skipped (${query}/${page}): ${error.message}\n`); break; }
    }
  }
  for (const query of githubCodeQueries) {
    try {
      const result = await json(`https://api.github.com/search/code?per_page=100&q=${encodeURIComponent(query)}`, { headers: { ...githubHeaders(), Accept: 'application/vnd.github+json' } });
      for (const hit of result.items || []) {
        const branch = String(hit.html_url || '').match(/\/blob\/([^/]+)\//)?.[1];
        addRepo({ ...hit.repository, default_branch: hit.repository.default_branch || branch }, `GitHub代码:${query}`);
      }
    } catch (error) { process.stderr.write(`GitHub code search skipped (${query}): ${error.message}\n`); }
  }

  const relevant = [...repositories.values()].filter(({ repo, sources }) =>
    [...sources].some(source => source.startsWith('GitHub代码:')) ||
    /deepseek[-_ ]?harness|(^|[-_/ ])dsh([-_/ ]|$)|harness[-_ ]?(plugin|theme|skin)/i.test(`${repo.name} ${repo.description || ''} ${(repo.topics || []).join(' ')}`));
  process.stdout.write(`GitHub discovery: ${relevant.length} plausible repositories.\n`);
  return mapPool(relevant.sort((a, b) => a.repo.full_name.localeCompare(b.repo.full_name)), 6, async ({ repo, sources }) => {
    try {
      if (!repo.default_branch) repo = await json(repo.url, { headers: githubHeaders() });
      const manifest = await json(`https://raw.githubusercontent.com/${repo.full_name}/${encodeURIComponent(repo.default_branch || 'main')}/package.json`);
      if (!hasHarnessManifest(manifest) || manifest.name?.startsWith('@deepseek-ai/') || existingPackages.has(manifest.name)) return null;
      const docs = await githubReadmes(repo.full_name, repo.default_branch);
      let descriptionZh = /[\u3400-\u9fff]/.test(manifest.description || '') ? manifest.description : chineseSummary(docs.chineseReadme || docs.defaultReadme);
      return {
        id: safeId(repo.full_name), name: manifest.dsh?.displayName || manifest.name || repo.name,
        description: String(manifest.description || repo.description || '').slice(0, 600), descriptionZh: String(descriptionZh || '').slice(0, 600),
        descriptionSource: descriptionZh ? (/[\u3400-\u9fff]/.test(manifest.description || '') ? 'package.json 中文描述' : docs.chinesePath || 'README.md') : '',
        installSpec: `git+https://github.com/${repo.full_name}.git`, package: manifest.name || repo.name, version: manifest.version || 'git',
        repositoryUrl: repo.html_url, previewImageUrl: previewImage(docs.chineseReadme, docs.rawRoot, repo.full_name, repo.default_branch) || previewImage(docs.defaultReadme, docs.rawRoot, repo.full_name, repo.default_branch) || githubOpenGraphPreview(repo.full_name),
        sourceType: 'git', license: repo.license?.spdx_id || manifest.license || '', category: categoryOf(manifest, repo.name),
        verified: true, hasLifecycleScripts: lifecycle(manifest), requiresBuildApproval: lifecycle(manifest),
        popularity: Number(repo.stargazers_count) || 0, updatedAt: repo.updated_at || new Date(0).toISOString(), discoverySource: [...sources].slice(0, 3).join(' · ')
      };
    } catch { return null; }
  });
}

const expandVisualCategory = item => {
  if (item.category !== 'Plugin') return item.category;
  const name = `${item.package || ''} ${item.name || ''}`.toLowerCase();
  const description = `${item.description || ''} ${item.descriptionZh || ''}`.toLowerCase();
  const visualName = /(?:^|[/@-])(?:theme|themes|skin|skins|appearance|wallpaper|background|beautify)(?:[-/]|$)|pixel[-_]?ui|matugen|whale[-_]?(?:bg|background)|ui[-_]?(?:customizer|styler)|(?:^|[-_/])gal(?:[-_/]|$)/i;
  const visualDescription = /(?:theme|skin|wallpaper|appearance) (?:plugin|pack|studio|picker|editor)|(?:themed|主题风格的?) (?:client|web|conversation|界面)|主题插件|皮肤插件|换肤插件|壁纸插件|界面美化|主题调色板|palette bridge|custom appearance/i;
  return visualName.test(name) || visualDescription.test(description) ? 'Skin' : item.category;
};

const itemPreference = item =>
  Number(item?.verified) * 1_000_000 +
  (String(item?.package || '').startsWith('@dsh-external/') ? 0 : 100_000) +
  (String(item?.sourceType || '').toLowerCase() === 'npm' ? 50_000 : 0) +
  Math.min(Number(item?.popularity) || 0, 99_999) +
  (item?.descriptionZh ? 100 : 0) + (item?.previewImageUrl ? 10 : 0);

const dedupeItems = items => {
  const byInstall = new Map();
  for (const item of items.filter(Boolean)) {
    const key = String(item.installSpec || item.package || item.id || '').trim().toLowerCase();
    if (!key) continue;
    const current = byInstall.get(key);
    if (!current || itemPreference(item) > itemPreference(current)) byInstall.set(key, item);
  }
  const byPackage = new Map();
  for (const item of byInstall.values()) {
    const key = String(item.package || '').trim().toLowerCase();
    const current = byPackage.get(key);
    if (!current || itemPreference(item) > itemPreference(current)) byPackage.set(key, item);
  }
  const byId = new Map();
  for (const item of byPackage.values()) {
    const key = String(item.id || '').trim().toLowerCase();
    const current = byId.get(key);
    if (!current || itemPreference(item) > itemPreference(current)) byId.set(key, item);
  }
  return [...byId.values()];
};

const stableItems = items => dedupeItems(items)
  .filter(item => item?.package && !item.package.startsWith('@deepseek-ai/'))
  .sort((a, b) => Number(b.verified) - Number(a.verified) || b.popularity - a.popularity || a.id.localeCompare(b.id))
  .map(item => ({ ...item, category: expandVisualCategory(item) }))
  .map(item => Object.fromEntries(Object.entries(item).filter(([key]) => key !== 'previewImagePath').map(([key, value]) => [key, value ?? ''])));

const previous = JSON.parse(await readFile(outputPath, 'utf8'));
if (process.argv.includes('--reclassify-only')) {
  const items = stableItems(previous.items);
  const before = previous.items.filter(item => item.category === 'Skin').length;
  const after = items.filter(item => item.category === 'Skin').length;
  await writeFile(outputPath, `${JSON.stringify({ ...previous, generatedAt: new Date().toISOString(), generator: 'scripts/update-plugin-catalog.mjs', items }, null, 2)}\n`, 'utf8');
  process.stdout.write(`Catalog reclassified locally (${before} -> ${after} skins, ${items.length} total entries).\n`);
  process.exit(0);
}
const previousMap = new Map(previous.items.filter(item => item.sourceType === 'npm').map(item => [item.package, item]));
const npmResult = await npmCandidates(previousMap, Number(previous.enrichmentCursor) || 0);
const npm = npmResult.items;
const discoveredGit = await githubCandidates(new Set(npm.map(item => item.package)));
const combined = [...npm, ...discoveredGit];
for (const item of previous.items.filter(item => !item.package.startsWith('@deepseek-ai/')))
  if (!combined.some(candidate => candidate.package === item.package)) combined.push(item);
const deduplicated = [...new Map(combined.map(item => [item.package.toLowerCase(), item])).values()];
const items = stableItems(deduplicated);
if (!items.length) throw new Error('Discovery produced an empty catalog; refusing to replace the last-known-good index.');

const catalog = {
  schemaVersion: 1,
  generatedAt: new Date().toISOString(),
  generator: 'scripts/update-plugin-catalog.mjs',
  enrichmentCursor: npmResult.nextCursor,
  discoverySources: ['npm 多关键词分页', 'npm 依赖反向发现', 'GitHub 仓库与 Topic', 'GitHub package.json 代码检索'],
  items
};
if (JSON.stringify({ ...previous, generatedAt: '' }) === JSON.stringify({ ...catalog, generatedAt: '' })) {
  process.stdout.write(`Catalog unchanged (${items.length} verified entries).\n`);
  process.exit(0);
}
await writeFile(outputPath, `${JSON.stringify(catalog, null, 2)}\n`, 'utf8');
const chinese = items.filter(item => item.descriptionZh).length;
const previews = items.filter(item => item.previewImageUrl).length;
process.stdout.write(`Catalog updated (${items.length} verified entries, ${chinese} Chinese summaries, ${previews} previews).\n`);
