# CLAUDE.md — Suporte Provedor API

API .NET do agente de suporte de um provedor de internet (ISP) pequeno. O cliente chega pelo WhatsApp
(só resposta automática com o link), abre o chat no site e conversa com um agente de IA que consulta
sistemas do provedor por **ferramentas (tool use)**: segunda via, diagnóstico de conexão, paliativos e
abertura de chamado para visita técnica.

Projeto irmão: `G:\ProjetosTeste\SuporteProvedorSite` (Next.js). Este repo é só a API.

> Estado atual: **MVP local funcionando** (identificação, chat, 7 ferramentas, auditoria). ERP, Radius,
> ACS/OLT e NOC são **simulados** por tabelas no PostgreSQL.
> Repositório **público** (portfólio): só dados fictícios e nenhum segredo. Nada de produção.

## Rodando localmente
```bash
# banco (uma vez; ver "Banco de dados" abaixo) + connection string no user-secrets (já configurada nesta máquina)
cd src/SuporteProvedor.Api
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5180 dotnet run --no-launch-profile
```
- API em `http://localhost:5180`, `GET /health`. CORS liberado só para `http://localhost:3000` (`Cors:Origins`).
- Provedor de IA em `Agent:Provider`: `openrouter` (padrão; `OpenRouter:ApiKey`, `OpenRouter:Model`), `anthropic`
  (`Anthropic:ApiKey`) ou `fake` (modelo roteirizado por palavra-chave, sem chave). Testado com o modelo real via OpenRouter.
- Segredos: user-secrets **ou** `src/SuporteProvedor.Api/appsettings.Local.json` (no `.gitignore`). O Visual Studio desta
  máquina não lê os user-secrets, por isso o arquivo local existe. **Repositório público: nunca commitar chaves/senhas.**
- Testes: `dotnet test` (regra do desbloqueio em confiança e adaptador do OpenRouter).
- Cuidado no Windows/PowerShell 5.1: `Set-Content -Encoding utf8` grava BOM e `Get-Content` sem `-Encoding utf8` lê
  UTF-8 como ANSI (corrompe acentos). Edite arquivos com a ferramenta de edição, não com regex em PowerShell.

## Stack
- .NET 10 (SDK 10.0.x), ASP.NET Core Minimal APIs ou Controllers (decidir ao criar o projeto; preferir Minimal APIs)
- PostgreSQL 18 local — banco `suporte_provedor` (`localhost:5432`, usuário `postgres`; senha em user-secrets, nunca no repo)
- Acesso a dados: **Npgsql + Dapper** com SQL explícito (`Infrastructure/Db.cs`), sem ORM nem migrations no MVP.
  Colunas `timestamptz` são lidas como `DateTime` (UTC) nos records — Dapper não converte para `DateTimeOffset` no construtor.
- IA: Messages API da Anthropic chamada por **HttpClient direto** (`AnthropicModel`, sem SDK), atrás de `IAgentModel`.
  Modelo configurável (`Agent:Model`), padrão um modelo rápido/barato (Haiku). Chave em user-secrets (`Anthropic:ApiKey`).

## Banco de dados (abordagem MVP)
Schema mínimo, SQL-first, em `db/`:
- `db/001_schema.sql` — cria as tabelas (idempotente: `IF NOT EXISTS`)
- `db/002_seed.sql` — dados **fictícios** de teste

Recriar do zero:
```bash
psql -U postgres -h localhost -c "DROP DATABASE IF EXISTS suporte_provedor" -c "CREATE DATABASE suporte_provedor ENCODING 'UTF8'"
psql -U postgres -h localhost -d suporte_provedor -f db/001_schema.sql -f db/002_seed.sql
```

Tabelas:
| Grupo | Tabela | Papel |
|---|---|---|
| Simula ERP | `clientes`, `faturas` | cadastro e cobrança (situação `ativo`/`bloqueado_inadimplencia`/...) |
| Simula Radius/ACS/OLT | `conexoes` | online, último login, IP, status e sinal da ONU |
| Simula NOC | `incidentes` | falhas gerais por `bairro` |
| Do agente (permanentes) | `conversas`, `mensagens` | histórico + auditoria das chamadas de ferramenta (`payload` jsonb) |
| Do agente (permanentes) | `chamados`, `desbloqueios_confianca` | saídas das ações do agente |

Quando integrar o ERP real, só as tabelas "Simula ..." são substituídas (via adapters); as do agente ficam.

Clientes de teste (cada um cobre um cenário):
| Cliente | CPF | Nascimento | Cenário |
|---|---|---|---|
| Ana Souza | 11144477735 | 1990-03-15 | tudo ok, segunda via simples |
| Bruno Lima | 52998224725 | 1985-07-22 | bloqueado por inadimplência, 2 faturas vencidas, desbloqueio em confiança |
| Carla Dias | 39053344705 | 1992-11-02 | online com sinal óptico ruim (-29,8 dBm): lentidão → paliativos → visita |
| Diego Rocha | 11122233396 | 1978-01-30 | offline; bairro Centro com incidente aberto (queda geral) |

## Arquitetura
Um único projeto (`src/SuporteProvedor.Api`) organizado em pastas, dependências apontando para dentro:
```
Api/            ConversasEndpoints (rotas, DTOs)
Application/    AgentService (loop de tool use), SessaoService (identificação/token), Agent/ (tools, prompt, modelos)
Domain/         records (Models.cs) e regras puras (RegrasDesbloqueio)
Infrastructure/ Db.cs (Postgres; no MVP também simula ERP/Radius/NOC)
```
**Dívida consciente do MVP:** `AgentTools` chama `Db` direto. Ao integrar o ERP real, extrair ports/adapters
(`IClienteGateway`, `IFaturaGateway`, `IDiagnosticoGateway`, `IIncidenteGateway`, `IChamadoGateway`):
MVP = implementação sobre Postgres; produção = implementação sobre a API do ERP.

### Ferramentas do agente
| Ferramenta | Faz |
|---|---|
| `listar_faturas` | faturas em aberto/recentes do cliente da sessão |
| `segunda_via` | devolve PIX copia e cola / linha digitável de uma fatura |
| `status_conexao` | online/offline, último login, status e sinal da ONU |
| `verificar_incidente` | há incidente aberto no bairro do cliente? previsão |
| `desbloqueio_confianca` | libera temporariamente cliente bloqueado (regras no código) |
| `abrir_chamado` | cria chamado de visita técnica com o diagnóstico já levantado |
| `transferir_humano` | marca a conversa como `transferida_humano` |

Paliativos (reiniciar roteador, testar por cabo, trocar canal Wi-Fi etc.) ficam no **system prompt /
base de conhecimento** (`Application/Agent/Prompts`), não em ferramentas.

### Fluxo do chat
1. `POST /api/conversas` cria conversa (não identificada) → `{id}`.
2. `POST /api/conversas/{id}/identificar` `{cpf, dataNascimento}` → `{token, nome, situacao}`; o token (aleatório, só o
   SHA-256 fica em `conversas.token_hash`) vai no header `Authorization: Bearer`. Erro 401 genérico; 3ª falha → 429 e conversa encerrada.
3. `POST /api/conversas/{id}/mensagens` `{texto}` (Bearer) → `{resposta, status, protocolo}`. `AgentService` roda o loop de
   tool use (máx. `Agent:MaxIteracoes`), grava tudo em `mensagens` (blocos da Messages API em `payload`). Streaming SSE fica para depois.
4. `GET /api/conversas/{id}/mensagens` (Bearer) → histórico visível (user/assistant).
5. Encerra com transferência para humano (`status=transferida_humano`, novas mensagens → 409) ou segue aberta.

No MVP o chat **exige identificação antes** de qualquer mensagem (sem FAQ anônimo).

## Regras inegociáveis
- **Identidade vem da sessão, nunca do modelo.** Toda ferramenta recebe o `cliente_id` do contexto da
  conversa autenticada; o modelo não passa (nem pode trocar) CPF/ID de cliente nos argumentos.
- Sem token válido, não há conversa com o agente (401). Ferramentas só existem dentro de uma sessão identificada.
- Identificação: CPF + data de nascimento; máx. 3 tentativas por conversa (`tentativas_identificacao`),
  depois bloqueia a conversa. Mensagem de erro **genérica** (não revelar se o CPF existe).
- **Regras de negócio no código, não no prompt.** Ex.: desbloqueio em confiança — 1 por mês, até 3 dias,
  só se `bloqueado_inadimplencia`. O modelo apenas pede; o código decide.
- O modelo só executa **ações pré-definidas** (ferramentas). Nenhum comando livre em equipamento de rede.
- Escalar para humano em frustração, assunto fora do escopo ou falha repetida de ferramenta.
- Mascarar dados sensíveis no que vai para o modelo (CPF completo, e-mail) quando não necessário; logs sem PII.
- Rate limit por IP e por conversa; CORS restrito à origem do site; headers de segurança.
- Segredos só em user-secrets/variáveis de ambiente. Nunca commitar chave Anthropic nem senha de banco.
- Todas as chamadas de ferramenta (entrada/saída) auditadas em `mensagens.payload`.

## Convenções
- C# idiomático, `nullable enable`, `async` ponta a ponta, `CancellationToken` nos endpoints.
- Nomes de domínio em **português** (Cliente, Fatura, Chamado), alinhados às tabelas; código de infraestrutura em inglês quando for termo técnico.
- Banco: `snake_case`, PKs `bigint identity` (conversas = `uuid`), datas `timestamptz`.
- Erros de API em `ProblemDetails`. Respostas ao cliente em português do Brasil.
- Testes: xUnit; regras de negócio (desbloqueio, identificação) com testes unitários; fluxo do agente com cliente Anthropic falso.

## Comandos
```bash
dotnet build && dotnet test
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Database=suporte_provedor;Username=postgres;Password=..." --project src/SuporteProvedor.Api
dotnet user-secrets set "Anthropic:ApiKey" "..." --project src/SuporteProvedor.Api
```

## Roadmap
1. **MVP**: identificação + faturas/segunda via + status de fatura + FAQ + abrir chamado (poucas integrações).
2. Diagnóstico: `status_conexao` e `verificar_incidente` (hoje simulados → Radius/ACS/NOC reais).
3. Ações: desbloqueio em confiança, reboot remoto de ONU (TR-069/GenieACS).
4. Integração real com o ERP do provedor (IXC / MK-Auth / SGP / Voalle — **definir qual**).
5. Walled garden: liberar o domínio do chat para clientes bloqueados (Mikrotik/Radius, lado do provedor).
6. Painel de atendente para conversas `transferida_humano`.

## Pendências
- Testar com o modelo real da Anthropic (chave) e ajustar o system prompt (`Application/Agent/Prompts.cs`).
- Testes do fluxo do agente com `IAgentModel` falso; `BadHttpRequestException` hoje vira 500 (deveria ser 400).
- Definir ERP real do provedor e confirmar o que a API dele oferece.
- Domínio do chat e provedor de hospedagem (hoje tudo local).
- Política de retenção de conversas (LGPD).
