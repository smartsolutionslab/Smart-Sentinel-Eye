// Lint configuration for `e2e/` — the same surface `e2e/tsconfig.json` already
// declares for typecheck, so lint and typecheck cover the same files (#2219).
//
// The rule set is the apps' rule set, assembled the way all three app configs
// assemble it. Two plugins are absent: `eslint-plugin-react` and
// `eslint-plugin-react-hooks`. There is no JSX and no hook under `e2e/`, so
// every rule in both would be inert.
//
// The globals are two environments at once, because a Playwright spec is two
// environments at once: Node around the browser code it hands to
// `page.evaluate`.

import js from '@eslint/js';
import tseslint from '@typescript-eslint/eslint-plugin';
import tsparser from '@typescript-eslint/parser';
import prettier from 'eslint-config-prettier';

export default [
  js.configs.recommended,
  {
    files: ['e2e/**/*.ts', 'playwright.config.ts'],
    languageOptions: {
      parser: tsparser,
      parserOptions: { ecmaVersion: 'latest', sourceType: 'module' },
      globals: {
        process: 'readonly',
        console: 'readonly',
        setTimeout: 'readonly',
        clearTimeout: 'readonly',
        Buffer: 'readonly',
        URL: 'readonly',
        URLSearchParams: 'readonly',
        fetch: 'readonly',
        globalThis: 'readonly',
        window: 'readonly',
        document: 'readonly',
        Element: 'readonly',
        MutationObserver: 'readonly',
        requestAnimationFrame: 'readonly',
        performance: 'readonly',
        atob: 'readonly',
      },
    },
    plugins: { '@typescript-eslint': tseslint },
    rules: {
      ...tseslint.configs.recommended.rules,
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
    },
  },
  prettier,
];
