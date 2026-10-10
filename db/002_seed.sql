-- Dados FICTÍCIOS para desenvolvimento. Cada cliente cobre um cenário de teste.

INSERT INTO clientes (nome, cpf, telefone, data_nascimento, plano, endereco, bairro, login_pppoe, situacao) VALUES
 ('Ana Souza',    '11144477735', '11911110001', '1990-03-15', '300 Mega', 'Rua das Flores, 100', 'Bela Vista', 'ana.souza',    'ativo'),
 ('Bruno Lima',   '52998224725', '11911110002', '1985-07-22', '500 Mega', 'Av. Brasil, 250',     'Jardim Sul', 'bruno.lima',   'bloqueado_inadimplencia'),
 ('Carla Dias',   '39053344705', '11911110003', '1992-11-02', '300 Mega', 'Rua do Sol, 45',      'Vila Nova',  'carla.dias',   'ativo'),
 ('Diego Rocha',  '11122233396', '11911110004', '1978-01-30', '200 Mega', 'Rua Verde, 12',       'Centro',     'diego.rocha',  'ativo')
ON CONFLICT (cpf) DO NOTHING;

-- Ana: tudo ok, fatura paga + próxima em aberto (cenário: segunda via simples)
-- Bruno: bloqueado, 2 faturas vencidas (cenário: inadimplência / desbloqueio em confiança)
-- Carla: online porém sinal óptico ruim (cenário: lentidão -> paliativos -> visita técnica)
-- Diego: offline, bairro Centro com incidente aberto (cenário: queda geral na região)
INSERT INTO faturas (cliente_id, referencia, valor, vencimento, status, pix_copia_cola, linha_digitavel, pago_em)
SELECT c.id, f.ref, f.valor, f.venc::date, f.st, 'PIX-SIMULADO-' || c.cpf || '-' || f.ref,
       '00000.00000 00000.000000 00000.000000 0 ' || replace(f.ref,'-',''), f.pago::timestamptz
FROM (VALUES
  ('11144477735','2026-09',99.90, CURRENT_DATE - 25, 'paga',   CURRENT_DATE - 26),
  ('11144477735','2026-10',99.90, CURRENT_DATE + 5,  'aberta', NULL),
  ('52998224725','2026-08',129.90,CURRENT_DATE - 55, 'aberta', NULL),
  ('52998224725','2026-09',129.90,CURRENT_DATE - 25, 'aberta', NULL),
  ('39053344705','2026-10',99.90, CURRENT_DATE + 8,  'aberta', NULL),
  ('11122233396','2026-10',79.90, CURRENT_DATE + 3,  'aberta', NULL)
) AS f(cpf, ref, valor, venc, st, pago)
JOIN clientes c ON c.cpf = f.cpf
ON CONFLICT (cliente_id, referencia) DO NOTHING;

INSERT INTO conexoes (cliente_id, online, ultimo_login, ip, onu_status, sinal_onu_dbm)
SELECT c.id, x.online, now() - x.ago::interval, x.ip::inet, x.onu, x.sinal
FROM (VALUES
  ('11144477735', true,  '3 days',   '10.10.0.11',  'ok',      -19.5),
  ('52998224725', false, '20 days',  NULL,          'ok',      -20.1),
  ('39053344705', true,  '1 day',    '10.10.0.33',  'ok',      -29.8),
  ('11122233396', false, '2 hours',  NULL,          'offline', NULL)
) AS x(cpf, online, ago, ip, onu, sinal)
JOIN clientes c ON c.cpf = x.cpf
ON CONFLICT (cliente_id) DO NOTHING;

INSERT INTO incidentes (bairro, titulo, descricao, previsao_em)
SELECT 'Centro', 'Rompimento de fibra no Centro',
       'Equipe técnica em campo para reparo do cabo principal.', now() + interval '3 hours'
WHERE NOT EXISTS (SELECT 1 FROM incidentes WHERE bairro='Centro' AND status='aberto');
