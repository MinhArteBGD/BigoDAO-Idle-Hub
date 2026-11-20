# BigoDAO Idle Hub — v0.9.2 Public Beta candidate

PT: Aplicativo Windows para organizar jogos web idle em perfis locais independentes. Lista reduz o alvo de memória; Grade e Individual permitem visualizar jogos. Não garante farm em segundo plano, consumo específico ou compatibilidade com todos os jogos. Sem servidor próprio, cadastro ou telemetria própria.

EN: Windows desktop app for idle web games with separate local profiles. List mode requests lower memory usage; Grid and Individual modes display games. Background farming, memory savings and compatibility are not guaranteed. No application-owned server, registration or telemetry.

## Build / Compilação
Windows 10/11 x64, .NET 8 SDK and Microsoft Edge WebView2 Evergreen Runtime. Run `dotnet run` for testing. To build a standalone release: `dotnet publish -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o release`. Distribute the entire release folder, not just the exe. Users still need WebView2 Runtime. Unsigned builds may show SmartScreen warnings. Do not bypass security alerts blindly.

Candidate: not compiled or tested on Windows after security changes. See TESTING.md, PRIVACY.md, SECURITY.md and DISCLAIMER.md. Source-available, noncommercial license: see LICENSE and LICENSE-PT.md. Final release audit pending.


### v0.9.2 Beta
- Testar criar, renomear, excluir grupos vazios e com contas, mover contas e reiniciar. Excluir grupo mantém perfis e contas em Sem grupo.
- Test blocked download and popup notices, group operations, restart persistence and dark selectors.

## Official distribution / Distribuição oficial
PT: Aplicativo gratuito. Versões modificadas por terceiros não são oficiais. Baixe somente pelos canais oficiais anunciados pela BigoDAO.

EN: Free application. Third-party modified versions are not official or endorsed. Download only through official channels announced by BigoDAO.
