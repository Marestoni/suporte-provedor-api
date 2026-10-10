using SuporteProvedor.Api.Application;

namespace SuporteProvedor.Tests;

public class ConversaLockTests
{
    [Fact]
    public void Segunda_entrada_na_mesma_conversa_e_recusada_ate_a_primeira_sair()
    {
        var trava = new ConversaLock();
        var id = Guid.NewGuid();

        var primeira = trava.TentarEntrar(id);
        Assert.NotNull(primeira);
        Assert.Null(trava.TentarEntrar(id));

        primeira.Dispose();
        Assert.NotNull(trava.TentarEntrar(id));
    }

    [Fact]
    public void Conversas_diferentes_nao_se_bloqueiam()
    {
        var trava = new ConversaLock();
        Assert.NotNull(trava.TentarEntrar(Guid.NewGuid()));
        Assert.NotNull(trava.TentarEntrar(Guid.NewGuid()));
    }
}