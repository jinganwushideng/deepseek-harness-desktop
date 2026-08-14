import { createRequire } from 'node:module'
import { readFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { pathToFileURL } from 'node:url'

const runtimeApp = process.argv[2]
const dshHome = process.argv[3]
if (!runtimeApp || !dshHome) throw new Error('helper requires runtime app and DSH_HOME')
const require = createRequire(join(runtimeApp, 'package.json'))
const YAML = require('yaml')
const atomicModule = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-atomic-write')).href)
const bootModule = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-app-boot')).href)

let input = ''
for await (const chunk of process.stdin) input += chunk
const request = JSON.parse(input || '{}')
const profileDir = join(dshHome, 'profiles', 'web')

const safeRead = async path => {
  try { return await readFile(path, 'utf8') } catch (error) { if (error?.code === 'ENOENT') return undefined; throw error }
}
const collectRows = async (bundle, profileRequire) => {
  let manifestPath
  try { manifestPath = require.resolve(`${bundle}/package.json`) } catch { manifestPath = profileRequire.resolve(`${bundle}/package.json`) }
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'))
  const patchPath = join(dirname(manifestPath), manifest?.dsh?.bundle?.patch ?? 'cordis.patch.yml')
  const text = await readFile(patchPath, 'utf8')
  const lines = text.split(/\r?\n/)
  const rows = []
  for (let index = 0; index < lines.length; index++) {
    const id = /^\s*-\s+id:\s*['"]?([^'"#\s]+)['"]?/.exec(lines[index])?.[1]
    if (!id) continue
    let name = ''
    for (let next = index + 1; next < Math.min(lines.length, index + 12); next++) {
      if (/^\s*-\s+(?:id|insert):/.test(lines[next])) break
      const found = /^\s+name:\s*['"]?([^'"#\s]+)['"]?/.exec(lines[next])?.[1]
      if (found) { name = found; break }
    }
    rows.push({ id, package: name, source: bundle })
  }
  return rows
}
const dependencyExportsBundle = async (packageName, profileRequire) => {
  let manifestPath
  try { manifestPath = profileRequire.resolve(`${packageName}/package.json`) }
  catch {
    let cursor = dirname(profileRequire.resolve(packageName))
    for (;;) {
      const candidate = join(cursor, 'package.json')
      const text = await safeRead(candidate)
      if (text) {
        const manifest = JSON.parse(text)
        if (manifest?.name === packageName) { manifestPath = candidate; break }
      }
      const parent = dirname(cursor)
      if (parent === cursor || !cursor.startsWith(profileDir)) break
      cursor = parent
    }
  }
  if (!manifestPath) return false
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'))
  return manifest?.dsh?.bundle?.patch !== undefined
}

let response
switch (request.op) {
  case 'patch.setDisabled': {
    const patchFile = request.path
    const text = await safeRead(patchFile)
    const entries = text ? YAML.parse(text) : []
    if (!Array.isArray(entries)) throw new Error('launcher patch must be an array')
    const filtered = entries.filter(item => item?.id !== request.id)
    if (request.disabled) filtered.push({ id: request.id, disabled: true })
    await atomicModule.writeFileAtomic(patchFile, YAML.stringify(filtered), { mode: 0o600, dirMode: 0o700 })
    response = { ok: true, disabled: request.disabled }
    break
  }
  case 'profile.inspect': {
    const manifestText = await safeRead(join(profileDir, 'package.json'))
    const manifest = manifestText ? JSON.parse(manifestText) : { dependencies: {}, dsh: { profile: { bundles: ['@deepseek-ai/dsh-base', '@deepseek-ai/dsh-web-app'] } } }
    const bundles = manifest?.dsh?.profile?.bundles ?? []
    const profileRequire = createRequire(join(profileDir, 'package.json'))
    const patch = YAML.parse(await safeRead(request.patchPath) ?? '[]') ?? []
    const disabled = new Set(Array.isArray(patch) ? patch.filter(item => item?.disabled === true).map(item => item.id) : [])
    const rows = []
    for (const bundle of bundles) {
      try { rows.push(...(await collectRows(bundle, profileRequire)).map(row => ({ ...row, builtIn: bundle === '@deepseek-ai/dsh-base' || bundle === '@deepseek-ai/dsh-web-app', disabled: disabled.has(row.id) }))) }
      catch (error) { rows.push({ id: bundle, package: bundle, source: bundle, builtIn: false, disabled: false, error: String(error) }) }
    }
    response = { bundles, dependencies: manifest.dependencies ?? {}, rows }
    break
  }
  case 'plugin.reconcile': {
    const manifest = bootModule.readProfileManifest('dsh', profileDir)
    const beforeDependencies = new Set(Array.isArray(request.beforeDependencies) ? request.beforeDependencies : [])
    const dependencies = Object.keys(manifest.dependencies ?? {})
    const dependencySet = new Set(dependencies)
    const profileRequire = createRequire(join(profileDir, 'package.json'))
    const bundleState = new Map()
    for (const packageName of dependencies) bundleState.set(packageName, await dependencyExportsBundle(packageName, profileRequire))
    const bundles = [...(manifest?.dsh?.profile?.bundles ?? [])]
    const added = []
    const removed = []
    const plain = []
    for (const packageName of dependencies) {
      if (bundleState.get(packageName)) {
        if (!bundles.includes(packageName)) { bundles.push(packageName); added.push(packageName) }
      } else plain.push(packageName)
    }
    for (const packageName of [...bundles]) {
      const wasDependency = beforeDependencies.has(packageName) || dependencySet.has(packageName)
      const stillBundle = dependencySet.has(packageName) && bundleState.get(packageName) === true
      if (wasDependency && !stillBundle) { bundles.splice(bundles.indexOf(packageName), 1); removed.push(packageName) }
    }
    manifest.dsh = { ...(manifest.dsh ?? {}), profile: { ...(manifest.dsh?.profile ?? {}), bundles } }
    bootModule.writeProfileManifest(profileDir, manifest)
    response = { ok: true, bundles, added, removed, plain }
    break
  }
  default: throw new Error(`unknown helper operation: ${request.op}`)
}
process.stdout.write(JSON.stringify(response))
