import { existsSync, readFileSync } from 'node:fs'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const output = resolve(root, '../ScpSlPanel.Api/wwwroot')
const indexPath = resolve(output, 'index.html')
if (!existsSync(indexPath)) throw new Error('Production index.html is missing; run the build first.')
const html = readFileSync(indexPath, 'utf8')
for (const match of html.matchAll(/(?:src|href)="(\/assets\/[^"]+)"/g)) {
  if (!existsSync(resolve(output, match[1].slice(1)))) throw new Error(`Missing built asset: ${match[1]}`)
}
const source = readFileSync(resolve(root, 'src/App.tsx'), 'utf8')
for (const marker of ['CONTINUE WITH DISCORD', 'PERSONAL SETTINGS', 'System health', 'Confirm your identity']) {
  if (!source.includes(marker)) throw new Error(`Expected application flow is missing: ${marker}`)
}
console.log('Frontend smoke checks passed: production entry, hashed assets, authentication, personal settings, and health UI.')
