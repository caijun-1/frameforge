# Release signing

FrameForge releases are code-signed, so Windows can show a verified publisher and
users can confirm the file has not been modified after release. This page documents
what is signed, how it is built, and how to verify a download.

## Who signs

| | |
| --- | --- |
| Maintainer | Cai Jun (individual developer) |
| GitHub | [@caijun-1](https://github.com/caijun-1) |
| Certificate | open-source code signing certificate issued to the maintainer |
| Scope of use | FrameForge release binaries only |

## What gets signed

Only an executable produced by this repository's CI from a tagged commit:

1. a tag matching `v*` is pushed to `main`;
2. [`.github/workflows/release.yml`](.github/workflows/release.yml) builds from
   source on `windows-latest` with `build.ps1`;
3. that build artifact is submitted to the signing service;
4. the signed `FrameForge.exe` is published to GitHub Releases.

No locally built binary, no third-party file and no re-uploaded asset is ever
signed. If a release asset fails verification, do not run it — open an issue.

## Verify a download

```powershell
Get-AuthenticodeSignature .\FrameForge.exe |
    Format-List Status, StatusMessage, SignerCertificate
```

Or, with the Windows SDK installed:

```powershell
signtool verify /pa /v FrameForge.exe
```

Expected: `Status : Valid` and signer `Cai Jun`. Every release also ships a
`FrameForge.exe.sha256` file with the SHA-256 of the signed executable:

```powershell
(Get-FileHash .\FrameForge.exe -Algorithm SHA256).Hash.ToLower()
```

## Rebuild it yourself

```powershell
git clone https://github.com/caijun-1/frameforge
cd frameforge
git checkout v1.2.0
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\FrameForge.exe --selftest
```

The build needs only the .NET Framework compiler that ships with Windows — no
Visual Studio, no .NET SDK, no package restore, no network access, no binary blobs
in the repository. The PE timestamp and the signature differ from the official
build, so byte-identical output is not expected; the source tree is the only input,
and `--selftest` exercises the sensor, topology, profile and session modules.

## What the software does and does not do

FrameForge is a performance tool for the user's own machine.

It does:

- read public performance counters, CPU/GPU sensors and (via Intel PresentMon) the
  frame-presentation events of the foreground game;
- change system settings the user already controls — process priority, power plan,
  timer resolution, Game DVR / fullscreen optimization, core affinity, interrupt
  affinity — each with a documented rollback path;
- apply a desktop-wide colour matrix through the Windows Magnification API.

It does not:

- inject into games, hook other processes or read game memory;
- install a kernel driver or a service;
- modify, patch or bypass any game file, anti-cheat or DRM component;
- bundle third-party binaries — PresentMon is downloaded by the user from Intel and
  is not redistributed here;
- collect telemetry or upload data. The only network request is the PresentMon
  download the user starts explicitly.

## Contact

Code signing questions: 846287098@qq.com

---

## 中文摘要

- **谁签**：维护者 Cai Jun（个人），使用开源代码签名证书，只签 FrameForge 发布件。
- **签什么**：只签 CI 从 tag 构建出的 `FrameForge.exe`，本地编译的文件不签。
- **怎么验**：`Get-AuthenticodeSignature .\FrameForge.exe`，状态应为 `Valid`，签名者为 `Cai Jun`；每个 Release 另附 `.sha256` 校验文件。
- **怎么复现**：`build.ps1` 只用系统自带的 .NET Framework 编译器，无依赖、无联网、仓库内无二进制。
- **不做什么**：不注入游戏、不读游戏内存、不装驱动、不改游戏文件、不收集数据。