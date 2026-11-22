# qrcodegen (vendored)

QR Code generator library (TypeScript) by Project Nayuki, MIT License (see the header of `qrcodegen.ts`).

- Source: https://github.com/nayuki/QR-Code-generator/blob/8329a7108fc22be3e1eec0a9f9318978579e3621/typescript-javascript/qrcodegen.ts
- Upstream file SHA-256 (41,022 bytes): `1dc03fb5a10e0e2318ea162755bbdb9977ca6ce52cff959e9c9b6deafdccda9c`
- Local changes, nothing else differs from upstream:
  - One line prepended: `// @ts-nocheck …`. The upstream code does not compile under our `noUncheckedIndexedAccess`. Its types still flow to importers.
  - Three lines appended (a blank line, a comment, `export default qrcodegen;`), so the file can be imported as an ES module.

To check: remove the first line and the last three lines, then compare the SHA-256 with the value above.
