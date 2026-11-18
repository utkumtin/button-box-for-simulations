// Generates src/generated/protocol.ts from /schemas (the single source of truth). Never commit the output.
import { compile } from 'json-schema-to-typescript';
import { mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const schemasDir = resolve(here, '../../../../../schemas');
const out = resolve(here, '../src/generated/protocol.ts');

// One synthetic root that references every schema file, so shared definitions are declared once.
const definitions = Object.fromEntries(
  readdirSync(schemasDir)
    .filter((f) => f.endsWith('.schema.json') && !f.startsWith('common.') && !f.startsWith('protocol.'))
    .sort()
    .map((f) => [f.split('.')[0], { $ref: f }]),
);
definitions.clientMessage = { $ref: 'protocol.schema.json#/definitions/clientMessage' };
definitions.hostMessage = { $ref: 'protocol.schema.json#/definitions/hostMessage' };

const ts = await compile({ title: 'Root', definitions }, 'Root', {
  cwd: schemasDir + '/',
  unreachableDefinitions: true,
  declareExternallyReferenced: true,
  additionalProperties: false,
  maxItems: -1,
  bannerComment: '/* Generated from /schemas by scripts/generate.mjs. Do not edit. */',
  style: { singleQuote: true },
});

mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, ts.replace(/\r\n/g, '\n'));
