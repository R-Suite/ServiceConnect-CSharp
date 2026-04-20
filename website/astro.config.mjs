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
      components: {
        Footer: './src/overrides/Footer.astro',
      },
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
            {
              label: 'Core Concepts',
              items: [
                { label: 'The Bus', link: '/learn/core-concepts/the-bus/' },
                { label: 'Messages', link: '/learn/core-concepts/messages/' },
                { label: 'Handlers', link: '/learn/core-concepts/handlers/' },
                { label: 'Endpoints', link: '/learn/core-concepts/endpoints/' },
              ],
            },
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
