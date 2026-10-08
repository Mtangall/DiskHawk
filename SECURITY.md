# Security policy

DiskHawk runs with administrator rights on many machines, so security reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting
(**Security → Report a vulnerability** on this repository) and include:

- the affected version (`DiskHawk.exe` → About),
- what an attacker needs (e.g. a standard user on a scanned machine, a crafted report file),
- steps to reproduce in a lab and the impact.

You should get an answer within a few days. Fixes are released as a new version and credited in the changelog unless you prefer otherwise.

## Supported versions

Only the latest release receives security fixes.

## Scope notes

- Reports (`.dhr`), deletion results and progress files coming from scanned machines are treated as untrusted input.
- Remote deletion is irreversible by design; issues where it could act on a path other than the one confirmed by the user are high priority.
