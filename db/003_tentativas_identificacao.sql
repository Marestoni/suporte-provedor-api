-- Falhas de identificação por CPF e por IP (limite que não é burlado criando conversas novas).
-- O CPF é guardado só como SHA-256 (hex) para não manter PII em claro numa tabela de log.

CREATE TABLE IF NOT EXISTS tentativas_identificacao (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    cpf_hash    CHAR(64)    NOT NULL,
    ip          INET,
    criado_em   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_tentativas_cpf ON tentativas_identificacao(cpf_hash, criado_em DESC);
CREATE INDEX IF NOT EXISTS ix_tentativas_ip  ON tentativas_identificacao(ip, criado_em DESC);
