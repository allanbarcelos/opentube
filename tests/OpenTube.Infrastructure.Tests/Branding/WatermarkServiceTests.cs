using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Infrastructure.Branding;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Branding;

[Collection(IntegrationCollection.Name)]
public class WatermarkServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.CreateVersion7();

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private WatermarkService Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db) => new(db, _relogio);

    [Fact]
    public async Task Sem_marca_definida_nao_ha_nada_a_exibir()
    {
        await using var db = postgres.CreateContext();

        Assert.Null(await Criar(db).GetInfoAsync());
        Assert.Null(await Criar(db).GetImageAsync());
    }

    [Fact]
    public async Task Define_a_marca_e_devolve_a_imagem_guardada()
    {
        await using (var db = postgres.CreateContext())
            await Criar(db).SaveAsync(PngDeTeste.Criar(400, 200), WatermarkPosition.TopRight, Admin);

        await using var leitura = postgres.CreateContext();
        var servico = Criar(leitura);
        var info = await servico.GetInfoAsync();
        var imagem = await servico.GetImageAsync();

        Assert.Equal(new WatermarkInfo(WatermarkPosition.TopRight, 400, 200, Agora.ToUnixTimeMilliseconds()), info);
        Assert.Equal((400, 200), PlayerWatermark.ReadPngSize(imagem!.Image));
    }

    [Fact]
    public async Task Imagem_grande_e_guardada_ja_no_tamanho_padrao()
    {
        await using (var db = postgres.CreateContext())
            await Criar(db).SaveAsync(PngDeTeste.Criar(1600, 600), WatermarkPosition.TopRight, Admin);

        await using var leitura = postgres.CreateContext();
        var marca = await leitura.PlayerWatermarks.SingleAsync();

        Assert.Equal((640, 240), (marca.Width, marca.Height));
        Assert.Equal((640, 240), PlayerWatermark.ReadPngSize(marca.Image));
    }

    [Fact]
    public async Task Enviar_outra_imagem_substitui_a_anterior_sem_criar_outra_linha()
    {
        await using (var db = postgres.CreateContext())
            await Criar(db).SaveAsync(PngDeTeste.Criar(400, 200), WatermarkPosition.TopRight, Admin);

        _relogio.Advance(TimeSpan.FromMinutes(5));

        await using (var db = postgres.CreateContext())
            await Criar(db).SaveAsync(PngDeTeste.Criar(300, 160), WatermarkPosition.BottomLeft, Admin);

        await using var leitura = postgres.CreateContext();
        var marca = await leitura.PlayerWatermarks.SingleAsync();

        Assert.Equal(300, marca.Width);
        Assert.Equal(WatermarkPosition.BottomLeft, marca.Position);
        Assert.Equal(Agora.AddMinutes(5), marca.UpdatedAt);
    }

    [Fact]
    public async Task Muda_so_a_posicao()
    {
        byte[] guardada;

        await using (var db = postgres.CreateContext())
            guardada = (await Criar(db).SaveAsync(PngDeTeste.Criar(), WatermarkPosition.TopLeft, Admin)).Image;

        await using (var db = postgres.CreateContext())
            await Criar(db).MoveAsync(WatermarkPosition.Center, Admin);

        await using var leitura = postgres.CreateContext();
        var marca = await leitura.PlayerWatermarks.SingleAsync();

        Assert.Equal(WatermarkPosition.Center, marca.Position);
        Assert.Equal(guardada, marca.Image);
    }

    [Fact]
    public async Task Mudar_a_posicao_sem_imagem_e_recusado()
    {
        await using var db = postgres.CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Criar(db).MoveAsync(WatermarkPosition.Center, Admin));
    }

    [Fact]
    public async Task Remove_a_marca()
    {
        await using (var db = postgres.CreateContext())
            await Criar(db).SaveAsync(PngDeTeste.Criar(), WatermarkPosition.TopLeft, Admin);

        await using var removendo = postgres.CreateContext();
        Assert.True(await Criar(removendo).RemoveAsync());
        Assert.False(await Criar(removendo).RemoveAsync());
        Assert.Null(await Criar(removendo).GetInfoAsync());
    }

    [Fact]
    public async Task O_banco_recusa_uma_segunda_marca()
    {
        await using var db = postgres.CreateContext();

        // A marca é uma configuração única: nem por fora da aplicação entra uma segunda.
        var erro = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_watermark (id, image, width, height, position, updated_by, updated_at) VALUES (2, '\\x00', 1, 1, 0, gen_random_uuid(), now())"));

        Assert.Equal("ck_player_watermark_singleton", erro.ConstraintName);
    }
}
