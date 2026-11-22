import { execFileSync, spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { HostInfo } from './host';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../..');
const project = path.join(repo, 'src/host/Tools/E2EHost/E2EHost.csproj');
const exe = path.join(repo, 'src/host/Tools/E2EHost/bin/Release/net10.0-windows/E2EHost.exe');
const clientDist = path.join(repo, 'src/web/apps/client/dist');

/** Builds and starts the test-only host; its single stdout line carries the ports, the control secret and the device tokens. */
export default async function globalSetup(): Promise<() => Promise<void>> {
  execFileSync('dotnet', ['build', project, '-c', 'Release', '--nologo', '-v', 'q'], { stdio: 'inherit' });
  const child: ChildProcessWithoutNullStreams = spawn(exe, [clientDist], { stdio: ['pipe', 'pipe', 'pipe'] });
  const exited = new Promise<number | null>((resolve) => child.once('exit', resolve));

  const info = await new Promise<HostInfo>((resolve, reject) => {
    let buffered = '';
    let stderr = '';
    const timer = setTimeout(() => reject(new Error('E2E host did not report ready in 60 s.')), 60_000);
    child.stderr.on('data', (d: Buffer) => (stderr += d.toString()));
    child.stdout.on('data', (d: Buffer) => {
      buffered += d.toString();
      const line = buffered.split('\n')[0];
      if (line !== undefined && buffered.includes('\n')) {
        clearTimeout(timer);
        try {
          resolve(JSON.parse(line) as HostInfo);
        } catch {
          reject(new Error('E2E host printed something that is not JSON.'));
        }
      }
    });
    void exited.then((code) => reject(new Error(`E2E host exited with ${code}. ${stderr}`)));
  });
  // Tokens stay in this process tree: workers inherit the environment.
  process.env.E2E_HOST = JSON.stringify(info);

  return async () => {
    child.stdin.end(); // EOF = stop; the host deletes its CA key and data folder
    const result = await Promise.race([exited, new Promise<'timeout'>((r) => setTimeout(() => r('timeout'), 15_000))]);
    if (result === 'timeout') child.kill();
  };
}
