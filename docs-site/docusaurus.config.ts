import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'Weir',
  tagline: 'Keep the audio and subtitle tracks you want. Drop the rest.',
  favicon: 'img/favicon.ico',

  future: {
    v4: true,
  },

  url: 'https://jampat000.github.io',
  baseUrl: '/Weir/',

  organizationName: 'jampat000',
  projectName: 'Weir',

  onBrokenLinks: 'throw',

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  markdown: {
    mermaid: true,
    hooks: {
      onBrokenMarkdownLinks: 'warn',
    },
  },

  themes: ['@docusaurus/theme-mermaid'],

  presets: [
    [
      'classic',
      {
        docs: {
          sidebarPath: './sidebars.ts',
          editUrl:
            'https://github.com/jampat000/Weir/tree/main/docs-site/',
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    colorMode: {
      defaultMode: 'dark',
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: 'Weir',
      logo: {
        alt: 'Weir',
        src: 'img/logo.svg',
        srcDark: 'img/logo-dark.svg',
      },
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'docsSidebar',
          position: 'left',
          label: 'Docs',
        },
        {
          type: 'docSidebar',
          sidebarId: 'apiSidebar',
          position: 'left',
          label: 'API',
        },
        {
          href: 'https://github.com/jampat000/Weir',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            {label: 'Quickstart', to: '/docs/quickstart'},
            {label: 'Docker', to: '/docs/deployment/docker'},
            {label: 'Windows Installer', to: '/docs/deployment/windows'},
          ],
        },
        {
          title: 'Project',
          items: [
            {
              label: 'GitHub',
              href: 'https://github.com/jampat000/Weir',
            },
            {
              label: 'Releases',
              href: 'https://github.com/jampat000/Weir/releases',
            },
            {
              label: 'Issues',
              href: 'https://github.com/jampat000/Weir/issues',
            },
          ],
        },
        {
          title: 'More',
          items: [
            {label: 'Architecture', to: '/docs/architecture/overview'},
            {label: 'Security', to: '/docs/guides/security'},
          ],
        },
      ],
      copyright: `Copyright (C) 2026 jampat000 and the Weir contributors. Licensed under AGPL-3.0-or-later.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['bash', 'powershell', 'python', 'docker', 'yaml', 'toml', 'ini'],
    },
  } satisfies Preset.ThemeConfig,
};

export default config;
