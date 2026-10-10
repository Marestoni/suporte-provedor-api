namespace SuporteProvedor.Api.Application;

/// <summary>
/// Garante uma única requisição em andamento por conversa. Sem isso, duas mensagens simultâneas
/// (duplo clique, duas abas) intercalam turnos e pares tool_use/tool_result no histórico.
/// Em memória: vale para uma instância da API. Com várias instâncias, trocar por lock no banco (pg_advisory_lock).
/// </summary>
public class ConversaLock
{
    private readonly HashSet<Guid> _emAndamento = [];

    /// <summary>Devolve um token a descartar ao terminar, ou null se já há requisição em andamento nesta conversa.</summary>
    public IDisposable? TentarEntrar(Guid conversaId)
    {
        lock (_emAndamento)
            return _emAndamento.Add(conversaId) ? new Saida(this, conversaId) : null;
    }

    private void Sair(Guid conversaId)
    {
        lock (_emAndamento) _emAndamento.Remove(conversaId);
    }

    private sealed class Saida(ConversaLock dono, Guid id) : IDisposable
    {
        public void Dispose() => dono.Sair(id);
    }
}
