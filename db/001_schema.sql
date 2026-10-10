-- Suporte Provedor — schema MVP (PostgreSQL)
-- No MVP as tabelas clientes/faturas/conexoes/incidentes SIMULAM o ERP/Radius/ACS/NOC.
-- Na integração real, elas são substituídas por adapters (ver CLAUDE.md da API).

CREATE TABLE IF NOT EXISTS clientes (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nome            TEXT        NOT NULL,
    cpf             CHAR(11)    NOT NULL UNIQUE,
    telefone        TEXT,
    email           TEXT,
    data_nascimento DATE        NOT NULL,            -- 2º fator de identificação no chat
    plano           TEXT        NOT NULL,
    endereco        TEXT,
    bairro          TEXT        NOT NULL,            -- liga cliente a incidentes por região
    login_pppoe     TEXT        NOT NULL UNIQUE,
    situacao        TEXT        NOT NULL DEFAULT 'ativo'
                    CHECK (situacao IN ('ativo','bloqueado_inadimplencia','suspenso','cancelado')),
    criado_em       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS faturas (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    cliente_id      BIGINT      NOT NULL REFERENCES clientes(id),
    referencia      CHAR(7)     NOT NULL,            -- 'YYYY-MM'
    valor           NUMERIC(10,2) NOT NULL,
    vencimento      DATE        NOT NULL,
    status          TEXT        NOT NULL DEFAULT 'aberta'
                    CHECK (status IN ('aberta','paga','cancelada')),
    pix_copia_cola  TEXT,
    linha_digitavel TEXT,
    pago_em         TIMESTAMPTZ,
    UNIQUE (cliente_id, referencia)
);
CREATE INDEX IF NOT EXISTS ix_faturas_cliente_status ON faturas(cliente_id, status);

-- Estado técnico (simula Radius/ACS/OLT). 1 linha por cliente.
CREATE TABLE IF NOT EXISTS conexoes (
    cliente_id      BIGINT PRIMARY KEY REFERENCES clientes(id),
    online          BOOLEAN     NOT NULL,
    ultimo_login    TIMESTAMPTZ,
    ip              INET,
    onu_status      TEXT        NOT NULL DEFAULT 'ok'
                    CHECK (onu_status IN ('ok','los','offline','desconhecido')),
    sinal_onu_dbm   NUMERIC(5,2),                    -- ideal entre -8 e -26
    atualizado_em   TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Falhas gerais por região (simula NOC/Zabbix).
CREATE TABLE IF NOT EXISTS incidentes (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bairro          TEXT        NOT NULL,
    titulo          TEXT        NOT NULL,
    descricao       TEXT,
    status          TEXT        NOT NULL DEFAULT 'aberto'
                    CHECK (status IN ('aberto','resolvido')),
    iniciado_em     TIMESTAMPTZ NOT NULL DEFAULT now(),
    previsao_em     TIMESTAMPTZ,
    resolvido_em    TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_incidentes_bairro_status ON incidentes(bairro, status);

CREATE TABLE IF NOT EXISTS conversas (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    cliente_id      BIGINT REFERENCES clientes(id),  -- NULL até identificar
    canal           TEXT        NOT NULL DEFAULT 'web',
    status          TEXT        NOT NULL DEFAULT 'aberta'
                    CHECK (status IN ('aberta','encerrada','transferida_humano')),
    tentativas_identificacao INT NOT NULL DEFAULT 0,
    token_hash      TEXT,                            -- SHA-256 do token de sessão (emitido ao identificar)
    criado_em       TIMESTAMPTZ NOT NULL DEFAULT now(),
    encerrado_em    TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS mensagens (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    conversa_id     UUID        NOT NULL REFERENCES conversas(id) ON DELETE CASCADE,
    papel           TEXT        NOT NULL CHECK (papel IN ('user','assistant','tool')),
    conteudo        TEXT,
    ferramenta      TEXT,                            -- nome da tool quando papel='tool' ou chamada
    payload         JSONB,                           -- args/resultado da tool (auditoria)
    criado_em       TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_mensagens_conversa ON mensagens(conversa_id, id);

CREATE TABLE IF NOT EXISTS chamados (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    protocolo       TEXT        NOT NULL UNIQUE,
    cliente_id      BIGINT      NOT NULL REFERENCES clientes(id),
    conversa_id     UUID        REFERENCES conversas(id),
    tipo            TEXT        NOT NULL DEFAULT 'visita_tecnica'
                    CHECK (tipo IN ('visita_tecnica','financeiro','outro')),
    descricao       TEXT        NOT NULL,
    diagnostico     TEXT,                            -- resumo do que o agente já verificou/tentou
    status          TEXT        NOT NULL DEFAULT 'aberto'
                    CHECK (status IN ('aberto','agendado','concluido','cancelado')),
    criado_em       TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Regra de negócio aplicada em código (1 por mês, até 3 dias); aqui só o histórico.
CREATE TABLE IF NOT EXISTS desbloqueios_confianca (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    cliente_id      BIGINT      NOT NULL REFERENCES clientes(id),
    fatura_id       BIGINT      REFERENCES faturas(id),
    conversa_id     UUID        REFERENCES conversas(id),
    liberado_ate    TIMESTAMPTZ NOT NULL,
    criado_em       TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_desbloqueios_cliente ON desbloqueios_confianca(cliente_id, criado_em DESC);
