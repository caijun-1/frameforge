# Security policy

## Supported versions

Only the latest release receives fixes. Older builds are not patched — upgrade to
the current tag before reporting a problem.

| Version | Supported |
| --- | --- |
| latest release | yes |
| older releases | no |

## Reporting a vulnerability

Email **846287098@qq.com** with:

- the FrameForge version (footer of the settings page, or the release tag);
- Windows version and whether the app ran elevated;
- reproduction steps and the relevant lines from `%LOCALAPPDATA%\FrameForge\log.txt`.

Please do not open a public issue for anything that could be exploited before a fix
is available. Expect an initial reply within a few days.

## Scope

This project is a single portable executable that runs with the privileges the user
grants it. In scope:

- privilege escalation beyond what the user approved;
- any path where the app writes outside `%LOCALAPPDATA%\FrameForge\`, the registry
  keys it documents, or the directories it must touch to apply a boost;
- signature or update verification weaknesses;
- denial of service caused by crafted `games.ini` / `profiles.ini` / `sessions.dat`
  files.

Out of scope:

- findings that require an already compromised machine or administrator rights;
- the absence of a kernel driver, service or self-updater (all by design);
- third-party components listed in [NOTICE](NOTICE); report those upstream.

## Design notes relevant to security

- no telemetry, no analytics, no network calls other than the PresentMon download
  the user starts explicitly;
- no injection into other processes, no game memory access, no driver;
- every system change made by the boost feature is reversible from the UI;
- release binaries are built by CI from tagged source — see [SIGNING.md](SIGNING.md).