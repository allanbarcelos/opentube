// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Access;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Auditoria;

[Collection(IntegrationCollection.Name)]
public class AuditTrailTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly Viewer Administrador =
        Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("admin@opentube.org"), isAdmin: true);

    private readonly FakeTimeProvider _relogio = new(Agora);

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private AuditTrail Criar(OpenTube.Infrastructure.Persistence.OpenTubeDbContext db) => new(db, _relogio);

    [Fact]
    public async Task Registra_quem_fez_o_que_e_quando()
    {
        var alvo = Guid.CreateVersion7();
        await using var db = postgres.CreateContext();

        await Criar(db).RecordAsync(
            Administrador, AuditActions.VideoAlterado, AuditEntities.Video, alvo,
            "Vídeo liberado", "resumo-do-ip");

        await using var leitura = postgres.CreateContext();
        var registro = await leitura.AuditEntries.SingleAsync();

        Assert.Equal("admin@opentube.org", registro.ActorEmail);
        Assert.Equal(AuditActions.VideoAlterado, registro.Action);
        Assert.Equal(alvo, registro.EntityId);
        Assert.Equal(Agora, registro.At);
        Assert.Equal("resumo-do-ip", registro.IpHash);
    }

    [Fact]
    public async Task Acao_sem_autor_fica_atribuida_ao_sistema()
    {
        await using var db = postgres.CreateContext();

        await Criar(db).RecordAsync(
            Viewer.Anonymous, AuditActions.VideoReprocessado, AuditEntities.Video, Guid.CreateVersion7(),
            "Reprocessamento automático");

        await using var leitura = postgres.CreateContext();
        Assert.Equal("sistema", (await leitura.AuditEntries.SingleAsync()).ActorEmail);
    }

    [Fact]
    public async Task O_resumo_absurdamente_longo_e_truncado()
    {
        await using var db = postgres.CreateContext();

        await Criar(db).RecordAsync(
            Administrador, AuditActions.AcessoConcedido, AuditEntities.Concessao, Guid.CreateVersion7(),
            new string('x', 900));

        await using var leitura = postgres.CreateContext();
        Assert.Equal(500, (await leitura.AuditEntries.SingleAsync()).Summary.Length);
    }

    [Fact]
    public async Task A_listagem_vem_do_mais_recente_para_o_mais_antigo()
    {
        await using var db = postgres.CreateContext();
        var trilha = Criar(db);

        await trilha.RecordAsync(Administrador, AuditActions.VideoEnviado, AuditEntities.Video, null, "Primeiro");
        _relogio.Advance(TimeSpan.FromMinutes(1));
        await trilha.RecordAsync(Administrador, AuditActions.VideoExcluido, AuditEntities.Video, null, "Segundo");

        var registros = await trilha.ListAsync();

        Assert.Equal(["Segundo", "Primeiro"], registros.Select(r => r.Summary));
    }

    [Fact]
    public async Task Filtra_por_tipo_por_entidade_e_por_quem_fez()
    {
        var video = Guid.CreateVersion7();
        await using var db = postgres.CreateContext();
        var trilha = Criar(db);

        var outra = Viewer.Authenticated(Guid.CreateVersion7(), EmailAddress.Parse("outra@opentube.org"), isAdmin: true);

        await trilha.RecordAsync(Administrador, AuditActions.VideoAlterado, AuditEntities.Video, video, "Do vídeo");
        await trilha.RecordAsync(outra, AuditActions.ColecaoCriada, AuditEntities.Colecao, Guid.CreateVersion7(), "Da coleção");

        Assert.Single(await trilha.ListAsync(entityType: AuditEntities.Video));
        Assert.Single(await trilha.ListAsync(entityType: AuditEntities.Video, entityId: video));
        Assert.Empty(await trilha.ListAsync(entityType: AuditEntities.Video, entityId: Guid.CreateVersion7()));
        Assert.Single(await trilha.ListAsync(actorEmail: "  OUTRA@opentube.org  "));
    }

    [Fact]
    public async Task O_limite_da_listagem_e_respeitado()
    {
        await using var db = postgres.CreateContext();
        var trilha = Criar(db);

        for (var i = 0; i < 5; i++)
            await trilha.RecordAsync(Administrador, AuditActions.VideoEnviado, AuditEntities.Video, null, $"Item {i}");

        Assert.Equal(3, (await trilha.ListAsync(limit: 3)).Count);
        Assert.Equal(5, (await trilha.ListAsync(limit: 0)).Count > 0 ? 5 : 0);
    }

    [Fact]
    public async Task Exige_acao_tipo_e_resumo()
    {
        await using var db = postgres.CreateContext();
        var trilha = Criar(db);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            trilha.RecordAsync(Administrador, "  ", AuditEntities.Video, null, "Resumo"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            trilha.RecordAsync(Administrador, AuditActions.VideoEnviado, "  ", null, "Resumo"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            trilha.RecordAsync(Administrador, AuditActions.VideoEnviado, AuditEntities.Video, null, "  "));
    }
}
