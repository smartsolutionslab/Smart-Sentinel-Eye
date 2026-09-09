// Guard for #2219 / spec 114.
//
// `pnpm lint` filtered to `./apps/**` for the whole life of the repository, so
// ESLint never read a file under `e2e/` — the directory the PR bodies quote a
// green lint run about. This asserts the scope, because the scope is the thing
// that silently went missing.
//
// Two kinds of assertion, and they are not equally strong:
//   * `every e2e file resolves a TypeScript rule set` asks ESLint itself, so it
//     covers a file added tomorrow that no config block happens to match.
//   * the `package.json` / `ci.yml` assertions read an artefact. They are here
//     because the defect being guarded *was* a script string, and a config that
//     nothing invokes is the same gap wearing a different hat.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import test from 'node:test';
import { ESLint } from 'eslint';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const end2endRoot = path.join(repositoryRoot, 'e2e');

const severityOf = (entry) => (Array.isArray(entry) ? entry[0] : entry);

async function typescriptFilesUnder(directory) {
  const entries = await readdir(directory, { withFileTypes: true, recursive: true });
  return entries
    .filter((entry) => entry.isFile() && entry.name.endsWith('.ts'))
    .map((entry) => path.join(entry.parentPath ?? entry.path, entry.name));
}

test('every TypeScript file under e2e/ is covered by the root ESLint configuration', async () => {
  const files = await typescriptFilesUnder(end2endRoot);
  assert.ok(files.length > 0, 'expected e2e/ to contain TypeScript files');

  const eslint = new ESLint({ cwd: repositoryRoot });
  const uncovered = [];

  for (const file of files) {
    if (await eslint.isPathIgnored(file)) {
      uncovered.push(`${path.relative(repositoryRoot, file)} (ignored)`);
      continue;
    }

    const configuration = await eslint.calculateConfigForFile(file);
    // A TypeScript rule proves the `e2e` block matched, not merely the global
    // `js.configs.recommended` entry, which matches every file in the repo.
    const severity = severityOf(configuration.rules?.['@typescript-eslint/no-explicit-any']);

    if (severity === undefined || severity === 'off' || severity === 0) {
      uncovered.push(`${path.relative(repositoryRoot, file)} (no TypeScript rules)`);
    }
  }

  assert.deepEqual(uncovered, [], `e2e files outside the lint scope: ${uncovered.join(', ')}`);
});

test('the root lint script runs the e2e leg', () => {
  const { scripts } = JSON.parse(readFileSync(path.join(repositoryRoot, 'package.json'), 'utf8'));

  assert.match(
    scripts.lint,
    /pnpm lint:e2e/,
    'package.json `lint` must chain the e2e leg, the way `typecheck` chains `typecheck:e2e`',
  );
  assert.match(scripts['lint:e2e'] ?? '', /\be2e\b/, 'package.json `lint:e2e` must target e2e/');
});

test('CI gates on the root lint and test scripts, not a re-spelled apps filter', () => {
  const workflow = readFileSync(path.join(repositoryRoot, '.github/workflows/ci.yml'), 'utf8');

  assert.match(workflow, /run: pnpm lint$/m, 'ci.yml must run the root `lint` script');
  assert.match(workflow, /run: pnpm test$/m, 'ci.yml must run the root `test` script');
  assert.doesNotMatch(
    workflow,
    /--filter "\.\/apps\/\*\*" (lint|test)/,
    'ci.yml must not re-spell the apps-only filter and bypass the root scripts',
  );
});
