-- Garante no banco que um cliente tem no máximo 1 chamado ativo (aberto ou agendado) por tipo.
-- A checagem em código (SELECT antes do INSERT) sozinha não impede duas conversas simultâneas do mesmo cliente.
CREATE UNIQUE INDEX IF NOT EXISTS ux_chamados_ativo_cliente_tipo
    ON chamados (cliente_id, tipo)
    WHERE status IN ('aberto', 'agendado');