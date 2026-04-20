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
            {
              label: 'Messaging Patterns',
              items: [
                { label: 'Pub/Sub', link: '/learn/messaging-patterns/pub-sub/' },
                { label: 'Point-to-Point', link: '/learn/messaging-patterns/point-to-point/' },
                { label: 'Request/Reply', link: '/learn/messaging-patterns/request-reply/' },
                { label: 'Competing Consumers', link: '/learn/messaging-patterns/competing-consumers/' },
                { label: 'Content-Based Routing', link: '/learn/messaging-patterns/content-based-routing/' },
                { label: 'Routing Slip', link: '/learn/messaging-patterns/routing-slip/' },
                { label: 'Scatter-Gather', link: '/learn/messaging-patterns/scatter-gather/' },
                { label: 'Process Manager', link: '/learn/messaging-patterns/process-manager/' },
                { label: 'Aggregator', link: '/learn/messaging-patterns/aggregator/' },
                { label: 'Filters', link: '/learn/messaging-patterns/filters/' },
                { label: 'Streaming', link: '/learn/messaging-patterns/streaming/' },
              ],
            },
            {
              label: 'Operations',
              items: [
                { label: 'Configuration', link: '/learn/operations/configuration/' },
                { label: 'Hosting & Lifecycle', link: '/learn/operations/hosting/' },
                { label: 'Error Handling', link: '/learn/operations/error-handling/' },
                { label: 'Observability', link: '/learn/operations/observability/' },
              ],
            },
          ],
        },
        {
          label: 'API Reference',
          items: [
            { label: 'Overview', link: '/reference/' },
            {
              label: 'Bus',
              items: [
                { label: 'IBus', link: '/reference/bus/ibus/' },
                { label: 'IBusConfiguration', link: '/reference/bus/ibusconfiguration/' },
                { label: 'AddServiceConnect', link: '/reference/bus/add-serviceconnect/' },
              ],
            },
            {
              label: 'Messages',
              items: [
                { label: 'Message', link: '/reference/messages/message/' },
                { label: 'Envelope', link: '/reference/messages/envelope/' },
                { label: 'Message options', link: '/reference/messages/options/' },
              ],
            },
            { label: 'Handlers', items: [] },
            { label: 'Configuration', items: [] },
            { label: 'Process Managers', items: [] },
            { label: 'Filters & Middleware', items: [] },
          ],
        },
        {
          label: 'Extension Points',
          items: [
            { label: 'Overview', link: '/reference/extension-points/' },
            { label: 'Persistence', items: [] },
            { label: 'Serialization', items: [] },
            { label: 'Transport', items: [] },
            { label: 'Registry', items: [] },
          ],
        },
        {
          label: 'Samples',
          link: '/samples/',
        },
        {
          label: 'Releases',
          link: '/releases/',
        },
      ],
    }),
  ],
});
