# Release gate / Checklist
- Back up local profiles privately with app closed; never publish backup. Close previous version before testing.
- Run `dotnet restore` then `dotnet list package --vulnerable --include-transitive` and `dotnet build -c Release`.
- Validate installed Evergreen runtime is patched; SDK version is not runtime version. Check .NET servicing updates.
- Test normal, list, grid, individual, eco and standby; check actual farm and isolated logins after restart. Test sites requiring popups, permissions or query parameters; these may break. Test harmless HTTPS download, popup, camera request and external protocol are blocked. Never test with real malware or valuable wallets.
- Build on a clean Windows account or neutral path. Do not distribute bin/obj, PDBs, profile folders, Git history or personal screenshots. Inspect binaries and entire release archive for personal names, emails, paths and credentials. Scan with Defender. Test on clean Windows with runtime installed; check no administrator requirement.
- Choose license, enable GitHub private security reporting, use GitHub noreply email, inspect staged files and commit metadata before pushing. Upload only reviewed source and release archive, generate SHA256 with `Get-FileHash`.
- Do not publish until these checks pass. No Windows compilation or dynamic test performed by assistant.

- PT/EN: Testar remover mantendo dados, cancelar e excluir dados em conta descartável; verificar pasta do perfil e outras contas. / Test retain, cancel and delete on a disposable account; verify profile folder and other accounts.


### v0.9.2 Beta
- Testar criar, renomear, excluir grupos vazios e com contas, mover contas e reiniciar. Excluir grupo mantém perfis e contas em Sem grupo.
- Test blocked download and popup notices, group operations, restart persistence and dark selectors.
