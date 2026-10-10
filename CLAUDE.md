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
- Testes: `dotnet test` (unitários). Testes de integração (banco temporário) só rodam com `SUPORTE_TEST_PG` definido; sem ela são pulados.
- Proxy: `Proxy:ProxiesConhecidos` (preferido) ou `Proxy:ConfiarEmQualquerOrigem`; chave do hash de CPF em `Seguranca:ChaveHashCpf`.
- Erros da API em ProblemDetails (`detail` = mensagem ao cliente); 409 = atendimento encerrado, 429 = ocupado/limite.
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
- `db/003_tentativas_identificacao.sql`, `db/004_chamado_unico_ativo.sql` — migrations (limite de identificação; chamado ativo único)

Recriar do zero:
```bash
psql -U postgres -h localhost -c "DROP DATABASE IF EXISTS suporte_provedor" -c "CREATE DATABASE suporte_provedor ENCODING 'UTF8'"
for f in db/*.sql; do psql -U postgres -h localhost -d suporte_provedor -v ON_ERROR_STOP=1 -f "$f"; done
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
- Identificação: CPF + data de nascimento; máx. 3 tentativas por conversa (`conversas.tentativas_identificacao`) **e**
  5 por CPF / 10 por IP em 15 min (tabela `tentativas_identificacao`, `PoliticaIdentificacao`) — o limite por conversa sozinho
  é burlável criando conversas novas (achado da security-review). Mensagem de erro **genérica** (não revelar se o CPF existe).
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

## Checklist ANTES DE QUALQUER DEPLOY (obrigatório)
Nenhum deploy (nem merge para a branch de produção) sem passar por todos os itens, nesta ordem. Quem for o agente/pessoa
que vai publicar executa e **registra o resultado** (achados e correções) no PR ou no histórico do commit.

1. **`/security-review`** — revisão de segurança das mudanças (identificação, sessão, IDOR nas ferramentas, PII, segredos, CORS/proxy).
   Corrigir todo achado de severidade média ou alta antes de seguir.
2. **`/code-review high`** — revisão de bugs (loop do agente, histórico, transações/concorrência, regras de negócio). **Repetir em loop**:
   corrigir os achados, reexecutar, e só parar quando restarem apenas achados de baixa severidade (as 6 voltas já feitas convergiram assim:
   10 → 10 → 9 → 7 → 5 → 2).
3. **`/simplify`** — limpeza (reuso, duplicação, complexidade) nas mudanças, **depois** das correções dos itens 1 e 2.
4. **`/run`** — subir API + site e exercitar os cenários de ponta a ponta (Ana, Bruno, Carla, Diego) no navegador, conferindo o fluxo real.
5. **Testes e build verdes:** `dotnet build`, `dotnet test` com `SUPORTE_TEST_PG` definido (os de integração **não podem** ficar pulados),
   e no site `npm run lint` + `npm run build`.
6. **Segredos:** varrer o que vai para o git (`git diff --cached`) atrás de chaves/senhas; `appsettings.Local.json` e `.env.local` nunca
   entram. Em produção configurar por variável de ambiente: `ConnectionStrings__Default`, chave do provedor de IA,
   `Seguranca__ChaveHashCpf`, `Proxy__ProxiesConhecidos`, `Cors__Origins`. Chave que já apareceu em chat/log deve ser **revogada**.
7. **Banco:** aplicar as migrations novas de `db/` em ordem no ambiente de destino antes de subir a nova versão da API.
8. **Documentação:** atualizar README, `docs/DATABASE.md` e este arquivo (seção "Já feito" e "Pendências") com o que mudou.

Repositório público: mesmo para portfólio, nada de dados reais de clientes nem credenciais, em nenhum commit (o histórico é público).

## Já feito (revisões security-review + code-review, 6 voltas)
- Limite de identificação por (CPF, IP) / IP / CPF em janela de 15 min, CPF com HMAC, IPv6 agrupado em /64, tentativa bloqueada não conta.
- Loop do agente robusto: histórico saneado e limitado, par `tool_use`/`tool_result` sempre fechado (transação), tool_use truncado não executa,
  turno vazio não é gravado, uma requisição por conversa (429), efeitos das ferramentas concluem mesmo se o cliente desconectar.
- Desbloqueio em confiança atômico (`FOR UPDATE`) e com expiração (situação efetiva `liberado_em_confianca`); chamado idempotente por cliente.
- Erros em ProblemDetails (400 para JSON inválido); datas em horário de Brasília; retenção horária de tentativas e conversas anônimas.
- Testes: unitários + integração (banco temporário, `SUPORTE_TEST_PG`) cobrindo lockout, desbloqueio, chamado e loop do agente.

## Pendências
- Ajustar o system prompt (`Application/Agent/Prompts.cs`) com conversas reais; testar mais cenários com o modelo pago (Haiku) — só o gratuito foi usado.
- Definir ERP real do provedor e confirmar o que a API dele oferece; extrair ports/adapters (hoje `AgentTools` chama `Db` direto).
- Domínio do chat e provedor de hospedagem (hoje tudo local); configurar `Seguranca:ChaveHashCpf` e `Proxy:ProxiesConhecidos` em produção.
- Lock por conversa é em memória (1 instância); com várias instâncias trocar por advisory lock do Postgres.
- Política de retenção de conversas identificadas (LGPD) — hoje só as anônimas e as tentativas são apagadas.
