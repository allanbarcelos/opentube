using OpenTube.Infrastructure.Storage;

namespace OpenTube.Infrastructure.Tests.Storage;

public class UploadPlanTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    [Fact]
    public void Arquivo_pequeno_vira_um_unico_pedaco()
    {
        var plano = UploadPlan.For(2 * MiB);

        Assert.Equal(UploadPlan.MinPartSize, plano.PartSizeBytes);
        Assert.Equal(1, plano.PartCount);
    }

    [Fact]
    public void Usa_o_pedaco_minimo_enquanto_couber_no_limite_de_pedacos()
    {
        var plano = UploadPlan.For(1 * GiB);

        Assert.Equal(UploadPlan.MinPartSize, plano.PartSizeBytes);
        Assert.Equal(205, plano.PartCount);
    }

    [Fact]
    public void Aumenta_o_pedaco_quando_o_arquivo_passaria_de_dez_mil_pedacos()
    {
        // 60 GiB em pedaços de 5 MiB daria 12.288 pedaços, acima do que o protocolo aceita.
        var plano = UploadPlan.For(60 * GiB);

        Assert.True(plano.PartSizeBytes > UploadPlan.MinPartSize);
        Assert.True(plano.PartCount <= UploadPlan.MaxParts);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(5L * 1024 * 1024)]
    [InlineData(700L * 1024 * 1024)]
    [InlineData(20L * 1024 * 1024 * 1024)]
    [InlineData(200L * 1024 * 1024 * 1024)]
    [InlineData(1024L * 1024 * 1024 * 1024)]
    public void Nunca_passa_do_limite_de_pedacos_do_protocolo(long tamanho)
    {
        var plano = UploadPlan.For(tamanho);

        Assert.InRange(plano.PartCount, 1, UploadPlan.MaxParts);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(700L * 1024 * 1024)]
    [InlineData(200L * 1024 * 1024 * 1024)]
    public void Os_pedacos_cobrem_o_arquivo_inteiro(long tamanho)
    {
        var plano = UploadPlan.For(tamanho);

        var cobertura = (long)plano.PartSizeBytes * plano.PartCount;

        Assert.True(cobertura >= tamanho, "os pedaços precisam cobrir o arquivo");
        Assert.True(cobertura - plano.PartSizeBytes < tamanho, "não deve sobrar um pedaço inteiro vazio");
    }

    [Fact]
    public void Pedaco_calculado_e_multiplo_de_um_mebibyte()
    {
        var plano = UploadPlan.For(300 * GiB);

        Assert.Equal(0, plano.PartSizeBytes % MiB);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Recusa_tamanho_invalido(long tamanho)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadPlan.For(tamanho));
    }
}
