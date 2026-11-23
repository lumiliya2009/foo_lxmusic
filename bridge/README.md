# Managed bridge

The bridge implements catalog queries, source-script execution, lyrics and downloads. The native component launches it as a separate process; requests use Base64URL-encoded arguments, and responses are tab-separated records on stdout.

This directory is licensed under **GPL-3.0-or-later**, except for files carrying separate notices. `Resources/user-api-preload.js` is derived from LX Music Mobile v1.8.4 and retains its Apache-2.0 license. See `LICENSE` and the parent directory's `THIRD_PARTY_NOTICES.md`.

Managed dependencies are embedded in `foo_lx_bridge.exe` and loaded on demand. Deploy the executable and its `.config` together; separate dependency DLLs are not required. The component package keeps the corresponding license notices.

Build:

```powershell
dotnet build ./foo_lx_bridge.csproj -c Release -p:RestoreLockedMode=true
```

Runtime user data is stored under the profile directory passed by the native component. Source scripts and account data are user-specific runtime files and are not part of this repository.
