// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// https://astro.build/config
export default defineConfig({
  site: 'https://r-suite.github.io',
  base: '/ServiceConnect-CSharp/',
  integrations: [
    starlight({
      title: 'ServiceConnect',
      description:
        'Asynchronous messaging for .NET. Distributed systems, done cleanly.',
      logo: {
        src: './src/assets/logo-icon.svg',
        replacesTitle: false,
      },
      customCss: ['./src/styles/brand.css'],
      social: [
        {
          icon: 'github',
          label: 'GitHub',
          href: 'https://github.com/R-Suite/ServiceConnect-CSharp',
        },
      ],
      sidebar: [
        {
          label: 'Learn',
          items: [
            { label: 'Getting Started', link: '/learn/getting-started/' },
          ],
        },
        {
          label: 'API Reference',
          link: '/api/',
        },
        {
          label: 'Samples',
          autogenerate: { directory: 'samples' },
        },
        {
          label: 'Releases',
          link: '/releases/',
        },
      ],
    }),
  ],
});
