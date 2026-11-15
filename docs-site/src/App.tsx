import React from 'react'

/**
 * The documentation site's home page (T-259): a plain, honest index of
 * what actually lives on the site — the landing, the metrics page, the
 * changelog and the user manual — plus the repository doors. No claims
 * beyond links: the pages it names are the pages the deploy builds, and
 * the route base ('/ZettaTrade/') comes from vite.config.ts.
 *
 * The site is static-first: every content page (landing, metrics,
 * changelog, manual) is a committed or generated HTML document under
 * public/, and this single React page is only the entry door.
 */
const sections: Array<{ href: string; title: string; text: string }> = [
  {
    href: 'landing/',
    title: 'Landing',
    text: 'What ZettaTrade is and what actually ships — with per-OS downloads from the latest release.',
  },
  {
    href: 'metrics/',
    title: 'Public metrics',
    text: 'Releases, downloads, stars, contributors, resolved issues — every number carries the link that re-derives it.',
  },
  {
    href: 'changelog/',
    title: 'Changelog',
    text: 'Rendered from CHANGELOG.md on the default branch: what each version carried.',
  },
  {
    href: 'manual/',
    title: 'User manual',
    text: 'Rendered from docs/USER-MANUAL.md on the default branch: install, first run, concepts, safety notes.',
  },
]

const repoDoors: Array<{ href: string; label: string }> = [
  { href: 'https://github.com/ZettaTrade/ZettaTrade', label: 'Repository' },
  { href: 'https://github.com/ZettaTrade/ZettaTrade/blob/main/docs/TASKS.md', label: 'The board (roadmap)' },
  { href: 'https://github.com/ZettaTrade/ZettaTrade/blob/main/docs/SECURITY.md', label: 'Security' },
  { href: 'https://github.com/ZettaTrade/ZettaTrade/releases', label: 'Releases' },
]

export default function App(): React.ReactElement {
  return (
    <main style={{ maxWidth: '60rem', margin: '0 auto', padding: '3rem 1.25rem 4rem' }}>
      <h1 style={{ margin: '0 0 0.25rem' }}>ZettaTrade documentation</h1>
      <p style={{ color: '#5f6672', margin: '0 0 2.5rem' }}>
        The static documentation site of the open-source (MIT) cross-platform desktop
        trading terminal. Everything here is generated from the repository or committed
        as static pages — no telemetry, no trackers, no third-party scripts.
      </p>
      <ul style={{ listStyle: 'none', padding: 0, margin: 0 }}>
        {sections.map((s) => (
          <li key={s.href} style={{ margin: '0 0 1.5rem' }}>
            <a href={s.href} style={{ fontWeight: 600, fontSize: '1.15rem' }}>
              {s.title}
            </a>
            <p style={{ margin: '0.25rem 0 0', color: '#5f6672' }}>{s.text}</p>
          </li>
        ))}
      </ul>
      <footer style={{ marginTop: '2.5rem', borderTop: '1px solid #8884', paddingTop: '1.5rem' }}>
        {repoDoors.map((d) => (
          <span key={d.href} style={{ marginRight: '1.25rem' }}>
            <a href={d.href}>{d.label}</a>
          </span>
        ))}
      </footer>
    </main>
  )
}
